using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace WingetInstaller;

public partial class MainWindow : Window
{
    public ObservableCollection<ProgramItem> Programs { get; } = new();
    CancellationTokenSource? _cts;
    Process? _activeProcess;
    CancellationTokenSource? _progressAnimationCts;
    bool _wingetReady;
    bool _wingetBusy;
    bool _wingetCheckComplete;
    bool _versionCheckRunning;

    readonly HashSet<string> _lastFailedIds =
        new(StringComparer.OrdinalIgnoreCase);

    // Linha dinâmica do download no próprio log (estilo terminal).
    int _liveLogStart = -1;
    string? _liveLogTime;

    CancellationTokenSource? _downloadFallbackCts;
    DateTime _downloadFallbackStarted;
    int _downloadSpinnerIndex;

    CancellationTokenSource? _installFallbackCts;
    DateTime _installFallbackStarted;
    int _installSpinnerIndex;

    public MainWindow()
    {
        InitializeComponent();
        ProgramsList.ItemsSource = Programs;
        Loaded += MainWindow_Loaded;
    }

    async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        // A interface aparece primeiro; a checagem do WinGet ocorre sem travar a janela.
        await Task.Delay(250);

        LoadPrograms();
        RefreshSelectAllButton();
        RefreshProgramCount();

        InstallButton.IsEnabled = false;
        OpenSearchButton.IsEnabled = false;
        ProgramsList.IsEnabled = false;
        FooterStatus.Text = "Verificando WinGet...";

        _ = RefreshWingetStatusAsync();
    }

    void RefreshProgramCount()
    {
        ProgressText.Text = Programs.Count == 1
            ? "1 programa"
            : $"{Programs.Count} programas";
    }

    void LoadPrograms()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "programas.json");

        // O arquivo é opcional. Na primeira execução o AppInstall inicia vazio.
        // Ele só será criado quando o usuário escolher "Adicionar ao programas.json".
        if (!File.Exists(path))
            return;

        try
        {
            var cfg = JsonSerializer.Deserialize<Config>(
                File.ReadAllText(path, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            foreach (var p in cfg?.programas ?? [])
                Programs.Add(new ProgramItem
                {
                    Name = p.nome,
                    Id = p.id,
                    InJson = true
                });
        }
        catch (Exception ex)
        {
            // Um programas.json corrompido não precisa interromper a abertura
            // do AppInstall com uma caixa modal. O erro fica registrado no log.
            AppendLog("ERRO: Não foi possível ler o programas.json.");
            AppendLog(ex.Message);
            FooterStatus.Text = "Erro no programas.json. Veja o log.";
        }
    }

    HashSet<string> GetJsonProgramIds()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "programas.json");
        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var cfg = JsonSerializer.Deserialize<Config>(
                File.ReadAllText(path, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return (cfg?.programas ?? [])
                .Select(p => p.id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    void RefreshJsonMembership()
    {
        var ids = GetJsonProgramIds();
        foreach (var item in Programs)
            item.InJson = ids.Contains(item.Id);
    }

    void RemoveFromJsonButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;

        if (sender is not FrameworkElement element || element.Tag is not ProgramItem item)
            return;

        var path = Path.Combine(AppContext.BaseDirectory, "programas.json");

        try
        {
            if (!File.Exists(path))
            {
                item.InJson = false;
                return;
            }

            var cfg = JsonSerializer.Deserialize<Config>(
                File.ReadAllText(path, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new Config();

            cfg.programas ??= new List<ProgramCfg>();

            int removed = cfg.programas.RemoveAll(p =>
                p.id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
            {
                item.InJson = false;
                FooterStatus.Text = $"{item.Name} não estava no programas.json.";
                return;
            }

            if (cfg.programas.Count == 0)
            {
                // Não mantém um JSON vazio: sem programas persistentes,
                // o arquivo deixa de ser necessário.
                File.Delete(path);
            }
            else
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(cfg, options) + Environment.NewLine,
                    new UTF8Encoding(false));
            }

            item.InJson = false;
            FooterStatus.Text = cfg.programas.Count == 0
                ? $"{item.Name} removido. programas.json excluído por estar vazio."
                : $"{item.Name} removido do programas.json.";
        }
        catch (Exception ex)
        {
            AppDialog.Show(this,
                $"Não foi possível remover o programa do programas.json.\n\n{ex.Message}");
        }
    }

    void RemoveProgramButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;

        if (sender is not FrameworkElement element || element.Tag is not ProgramItem item)
            return;

        Programs.Remove(item);

        RefreshSelectAllButton();
        RefreshInstallButton();
        RefreshUpdateAllButton();

        RefreshProgramCount();

        FooterStatus.Text = Programs.Count == 0
            ? "Nenhum programa na lista."
            : $"{item.Name} removido da lista.";
    }

    async void OpenSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null || _wingetBusy) return;

        if (!await EnsureWingetReadyAsync())
        {
            AppDialog.Show(this,
                "WinGet não está disponível neste Windows.\n\nUse o botão 'Instalar WinGet' e tente novamente.");
            return;
        }

        var search = new SearchWindow
        {
            Owner = this
        };

        if (search.ShowDialog() == true)
        {
            int added = 0;
            var addedItems = new List<ProgramItem>();

            foreach (var result in search.SelectedPrograms)
            {
                var existing = Programs.FirstOrDefault(p =>
                    p.Id.Equals(result.Id, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    existing.Selected = true;
                    continue;
                }

                var newItem = new ProgramItem
                {
                    Name = result.Name,
                    Id = result.Id,
                    Selected = true,
                    VersionText = $"Disponível: {result.Version}",
                    Status = "Selecionado",
                    StatusBrush = Brushes.DodgerBlue
                };

                Programs.Add(newItem);
                addedItems.Add(newItem);
                added++;
            }

            // Atualiza os botões "remover do JSON", inclusive para itens que
            // já estavam na lista antes de serem gravados no programas.json.
            RefreshJsonMembership();

            RefreshProgramCount();

            if (search.SavedToJson)
            {
                if (search.SavedToJsonCount > 0)
                {
                    FooterStatus.Text = $"{search.SavedToJsonCount} programa(s) salvo(s) no programas.json e adicionado(s) à lista.";
                }
                else
                {
                    FooterStatus.Text = added > 0
                        ? "Os programas já estavam no programas.json e foram adicionados à lista."
                        : "Os programas escolhidos já estavam no programas.json e na lista.";
                }
            }
            else
            {
                FooterStatus.Text = added > 0
                    ? $"{added} programa(s) adicionado(s) e selecionado(s)."
                    : "Os programas escolhidos já estavam na lista.";
            }

            if (addedItems.Count > 0)
                await RefreshProgramVersionsAsync(addedItems);
        }
        }


    static string? ResolveWingetPath()
    {
        string localAlias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");

        if (File.Exists(localAlias))
            return localAlias;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var dir in path.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, "winget.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Ignora entradas inválidas do PATH.
                }
            }
        }

        return null;
    }

    async Task<string?> GetWingetVersionAsync()
    {
        string? winget = ResolveWingetPath();
        if (string.IsNullOrWhiteSpace(winget))
            return null;

        // Toda a criação/execução do processo fica fora da thread da interface.
        // Em instalações quebradas do App Installer, iniciar winget.exe pode demorar;
        // por isso existe um timeout curto e o processo é encerrado se necessário.
        return await Task.Run(async () =>
        {
            try
            {
                var psi = new ProcessStartInfo(winget, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = psi };

                if (!process.Start())
                    return null;

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));

                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch { }

                    return null;
                }

                string output = (await outputTask).Trim();
                string error = (await errorTask).Trim();

                if (process.ExitCode != 0)
                    return null;

                string version = !string.IsNullOrWhiteSpace(output)
                    ? output
                    : error;

                return string.IsNullOrWhiteSpace(version)
                    ? null
                    : version;
            }
            catch
            {
                return null;
            }
        });
    }

    async Task<bool> EnsureWingetReadyAsync()
    {
        if (_wingetCheckComplete)
            return _wingetReady;

        await RefreshWingetStatusAsync();
        return _wingetReady;
    }

    async Task RefreshWingetStatusAsync()
    {
        if (_wingetBusy)
            return;

        _wingetCheckComplete = false;

        WingetButton.IsEnabled = false;
        WingetButton.Content = "Verificando WinGet...";
        WingetButton.Background = new SolidColorBrush(Color.FromRgb(27, 38, 52));
        WingetButton.Foreground = Brushes.White;

        InstallButton.IsEnabled = false;
        UpdateAllButton.IsEnabled = false;
        OpenSearchButton.IsEnabled = false;
        RefreshVersionsButton.IsEnabled = false;
        ProgramsList.IsEnabled = false;
        FooterStatus.Text = "Verificando WinGet...";

        string? version = await GetWingetVersionAsync();
        SetWingetUi(version);

        if (version is null)
            AppendLog("WinGet não instalado ou não disponível.");
        else
            await RefreshProgramVersionsAsync();
    }

    void SetWingetUi(string? version)
    {
        _wingetReady = version is not null;
        _wingetCheckComplete = true;

        if (_wingetReady)
        {
            WingetButton.Content = "✓ WinGet";
            WingetButton.ToolTip =
                $"WinGet instalado{(string.IsNullOrWhiteSpace(version) ? "" : $" • {version}")}";
            WingetButton.Background = new SolidColorBrush(Color.FromRgb(27, 105, 70));
            WingetButton.Foreground = Brushes.White;
            WingetButton.IsEnabled = !_wingetBusy;

            OpenSearchButton.IsEnabled = !_wingetBusy && _cts is null && !_versionCheckRunning;
            RefreshVersionsButton.IsEnabled = !_wingetBusy && _cts is null && !_versionCheckRunning;
            ProgramsList.IsEnabled = !_wingetBusy && _cts is null && !_versionCheckRunning;

            RefreshSelectAllButton();
            RefreshInstallButton();
            RefreshUpdateAllButton();
            RefreshRetryFailuresButton();

            if (_cts is null && !_versionCheckRunning)
                FooterStatus.Text =
                    $"WinGet disponível{(string.IsNullOrWhiteSpace(version) ? "." : $": {version}")}";
        }
        else
        {
            WingetButton.Content = "↓ Instalar WinGet";
            WingetButton.ToolTip =
                "WinGet não encontrado ou não funcional. Clique para instalar/atualizar o App Installer.";
            WingetButton.Background = new SolidColorBrush(Color.FromRgb(185, 120, 22));
            WingetButton.Foreground = Brushes.White;
            WingetButton.IsEnabled = !_wingetBusy;

            OpenSearchButton.IsEnabled = false;
            RefreshVersionsButton.IsEnabled = false;
            InstallButton.IsEnabled = false;
            SelectAllButton.IsEnabled = false;
            SelectUpdatesButton.IsEnabled = false;
            SelectMissingButton.IsEnabled = false;
            UpdateAllButton.IsEnabled = false;
            RetryFailuresButton.IsEnabled = false;
            ProgramsList.IsEnabled = false;

            if (_cts is null)
                FooterStatus.Text = "WinGet não instalado. Use o botão Instalar WinGet.";
        }
    }

    async void WingetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wingetBusy || _cts is not null)
            return;

        string? current = await GetWingetVersionAsync();
        if (current is not null)
        {
            SetWingetUi(current);
            AppendLog($"WinGet instalado • versão {current}");
            return;
        }

        AppendLog("WinGet não está instalado ou não está disponível para este usuário.");
        AppendLog("Iniciando instalação/reparo do WinGet...");

        _wingetBusy = true;
        WingetButton.IsEnabled = false;
        OpenSearchButton.IsEnabled = false;
        RefreshVersionsButton.IsEnabled = false;
        WingetButton.Content = "Instalando WinGet...";
        WingetButton.Background = new SolidColorBrush(Color.FromRgb(185, 120, 22));
        FooterStatus.Text = "Preparando instalação do WinGet...";
        AppendLog("WinGet não encontrado. Tentando registrar o App Installer existente...");

        // Pasta exclusiva por tentativa. Isso evita conflito com arquivos TEMP
        // deixados por uma execução anterior ou ainda bloqueados por outro processo.
        string work = Path.Combine(
            Path.GetTempPath(),
            $"AppInstall-WinGet-{Environment.ProcessId}-{Guid.NewGuid():N}");

        string bundle = Path.Combine(work, "Microsoft.DesktopAppInstaller.msixbundle");
        string depsZip = Path.Combine(work, "DesktopAppInstaller_Dependencies.zip");
        string depsDir = Path.Combine(work, "deps");

        try
        {
            // Primeiro tenta registrar/reparar o App Installer que já está no Windows.
            await RunPowerShellAsync(
                "$ErrorActionPreference='SilentlyContinue'; " +
                "$p=Get-AppxPackage Microsoft.DesktopAppInstaller | Sort-Object Version -Descending | Select-Object -First 1; " +
                "if($p){ Add-AppxPackage -DisableDevelopmentMode -Register ($p.InstallLocation + '\\AppxManifest.xml') }; " +
                "exit 0");

            await Task.Delay(900);
            string? version = await GetWingetVersionAsync();
            if (version is not null)
            {
                AppendLog($"WinGet registrado com sucesso • versão {version}");
                FooterStatus.Text = $"WinGet disponível: {version}";
                SetWingetUi(version);
                await RefreshProgramVersionsAsync();
                return;
            }

            Directory.CreateDirectory(work);

            AppendLog("Registro não foi suficiente. Baixando o App Installer oficial...");
            FooterStatus.Text = "Baixando App Installer / WinGet...";

            // Tenta primeiro o asset direto da release estável do projeto oficial.
            try
            {
                await DownloadFileWithProgressAsync(
                    "https://github.com/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle",
                    bundle,
                    "App Installer",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppendLog($"Download direto falhou ({ex.Message}). Tentando endereço alternativo...");
                await DownloadFileWithProgressAsync(
                    "https://aka.ms/getwinget",
                    bundle,
                    "App Installer",
                    CancellationToken.None);
            }

            AppendLog("Download do App Installer concluído. Tentando instalar...");
            FooterStatus.Text = "Instalando App Installer / WinGet...";
            WingetButton.Content = "Instalando WinGet...";

            var install = await InstallAppxAsync(bundle);

            // Se faltarem dependências, baixa e instala somente então.
            if (install.code != 0)
            {
                AppendLog("A instalação direta precisa de dependências. Baixando dependências oficiais...");
                FooterStatus.Text = "Baixando dependências do WinGet...";

                await DownloadFileWithProgressAsync(
                    "https://github.com/microsoft/winget-cli/releases/latest/download/DesktopAppInstaller_Dependencies.zip",
                    depsZip,
                    "Dependências",
                    CancellationToken.None);

                if (Directory.Exists(depsDir))
                    Directory.Delete(depsDir, true);

                ZipFile.ExtractToDirectory(depsZip, depsDir, true);

                await InstallWingetDependenciesAsync(depsDir);

                AppendLog("Dependências concluídas. Tentando instalar o App Installer novamente...");
                FooterStatus.Text = "Instalando App Installer / WinGet...";
                WingetButton.Content = "Instalando WinGet...";

                install = await InstallAppxAsync(bundle);
            }

            if (install.code != 0)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(install.text)
                        ? $"Add-AppxPackage retornou o código {install.code}."
                        : install.text.Trim());

            // Reforça o registro para o usuário atual.
            await RunPowerShellAsync(
                "$ErrorActionPreference='SilentlyContinue'; " +
                "$p=Get-AppxPackage Microsoft.DesktopAppInstaller | Sort-Object Version -Descending | Select-Object -First 1; " +
                "if($p){ Add-AppxPackage -DisableDevelopmentMode -Register ($p.InstallLocation + '\\AppxManifest.xml') }; " +
                "exit 0");

            version = null;
            for (int i = 0; i < 12 && version is null; i++)
            {
                await Task.Delay(750);
                version = await GetWingetVersionAsync();
            }

            if (version is null)
                throw new InvalidOperationException(
                    "O App Installer foi atualizado, mas o comando WinGet ainda não ficou disponível para este usuário. " +
                    "Feche e abra a sessão do Windows ou reinicie a máquina.");

            AppendLog($"WinGet instalado/atualizado com sucesso • versão {version}");
            FooterStatus.Text = $"WinGet pronto: {version}";
            SetWingetUi(version);
            await RefreshProgramVersionsAsync();
        }
        catch (Exception ex)
        {
            _wingetReady = false;
            AppendLog($"Falha ao instalar WinGet: {ex.Message}");
            FooterStatus.Text = "Não foi possível instalar o WinGet.";
            AppDialog.Show(this,
                $"Não foi possível instalar/atualizar o WinGet.\n\n{ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, true);
            }
            catch { }

            _wingetBusy = false;
            string? version = await GetWingetVersionAsync();
            SetWingetUi(version);
        }
    }

    static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        int unit = 0;

        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return $"{bytes:0.##} {units[unit]}";
    }

    async Task DownloadFileWithProgressAsync(
        string url,
        string destination,
        string label,
        CancellationToken token)
    {
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        // Nunca escreve diretamente no arquivo final.
        // Cada tentativa recebe um .part exclusivo e o arquivo final só aparece
        // depois que todos os streams foram fechados.
        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";

        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("AppInstall/1.0");

            using var response = await client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                token);

            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            long received = 0;
            int nextLog = 10;
            var started = Stopwatch.StartNew();
            var lastUi = TimeSpan.Zero;

            // Escopo próprio para garantir Dispose/DisposeAsync antes do File.Move.
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                128 * 1024,
                useAsync: true))
            {
                byte[] buffer = new byte[128 * 1024];
                int read;

                while ((read = await input.ReadAsync(
                    buffer.AsMemory(0, buffer.Length), token)) > 0)
                {
                    await output.WriteAsync(
                        buffer.AsMemory(0, read), token);

                    received += read;

                    if (started.Elapsed - lastUi < TimeSpan.FromMilliseconds(250))
                        continue;

                    lastUi = started.Elapsed;

                    double speed = started.Elapsed.TotalSeconds > 0
                        ? received / started.Elapsed.TotalSeconds
                        : 0;

                    if (total is > 0)
                    {
                        double pct = Math.Clamp(
                            received * 100d / total.Value, 0, 100);

                        WingetButton.Content = $"WinGet {pct:0}%";
                        FooterStatus.Text =
                            $"{label}: {pct:0}% • {FormatBytes(received)} / " +
                            $"{FormatBytes(total.Value)} • {FormatBytes(speed)}/s";

                        if (pct >= nextLog)
                        {
                            AppendLog(
                                $"{label}: {pct:0}% " +
                                $"({FormatBytes(received)} / {FormatBytes(total.Value)})");
                            nextLog += 10;
                        }
                    }
                    else
                    {
                        WingetButton.Content = "Baixando WinGet...";
                        FooterStatus.Text =
                            $"{label}: {FormatBytes(received)} • " +
                            $"{FormatBytes(speed)}/s";
                    }
                }

                await output.FlushAsync(token);
            }

            // Só manipula o destino depois que o .part foi totalmente fechado.
            if (File.Exists(destination))
            {
                try
                {
                    File.Delete(destination);
                }
                catch (IOException)
                {
                    // Se por algum motivo o destino estiver bloqueado, usa um nome
                    // final alternativo dentro da pasta TEMP exclusiva.
                    destination = Path.Combine(
                        Path.GetDirectoryName(destination)!,
                        Path.GetFileNameWithoutExtension(destination) +
                        "-" + Guid.NewGuid().ToString("N") +
                        Path.GetExtension(destination));
                }
            }

            File.Move(partial, destination);

            AppendLog(
                $"{label}: download concluído ({FormatBytes(received)}).");
        }
        catch
        {
            try
            {
                if (File.Exists(partial))
                    File.Delete(partial);
            }
            catch { }

            throw;
        }
    }

    async Task<(int code, string text)> InstallAppxAsync(string path)
    {
        string escaped = path.Replace("'", "''");
        return await RunPowerShellAsync(
            "$ErrorActionPreference='Stop'; " +
            $"Add-AppxPackage -Path '{escaped}' -ForceApplicationShutdown -ErrorAction Stop");
    }

    async Task InstallWingetDependenciesAsync(string depsRoot)
    {
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };

        string archDir = Path.Combine(depsRoot, arch);
        if (!Directory.Exists(archDir))
            throw new DirectoryNotFoundException(
                $"Dependências para a arquitetura {arch} não foram encontradas.");

        var packages = Directory.EnumerateFiles(archDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(p =>
                p.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (packages.Count == 0)
            throw new InvalidOperationException(
                $"Nenhuma dependência compatível foi encontrada para {arch}.");

        foreach (var package in packages)
        {
            string name = Path.GetFileName(package);
            FooterStatus.Text = $"Instalando dependência: {name}";
            AppendLog($"Instalando dependência: {name}");

            var result = await InstallAppxAsync(package);

            if (result.code == 0)
                AppendLog($"Dependência instalada: {name}");
            else
                AppendLog($"Dependência mantida/ignorada: {name}");
        }
    }

    async Task<(int code, string text)> RunPowerShellAsync(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); " +
            "$OutputEncoding=[Console]::OutputEncoding; " +
            command);
        return await RunCapturedProcessAsync(psi);
    }

    async Task<(int code, string text)> RunPowerShellFileAsync(string scriptPath)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        return await RunCapturedProcessAsync(psi, true);
    }

    async Task<(int code, string text)> RunCapturedProcessAsync(
        ProcessStartInfo psi, bool echoOutput = false)
    {
        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            if (echoOutput)
                Dispatcher.BeginInvoke(() => AppendLog(e.Data));
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            if (echoOutput)
                Dispatcher.BeginInvoke(() => AppendLog(e.Data));
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.ToString());
    }

    void ApplyVersionInfo(
        ProgramItem item,
        (string? installed, string? available) info,
        bool updateStatus)
    {
        item.InstalledVersion = info.installed;
        item.AvailableVersion = info.available;
        item.HasUpdate = info.installed is not null && info.available is not null;

        item.VersionText = info.installed is null
            ? "Não instalado"
            : info.available is null
                ? $"{info.installed}"
                : $"{info.installed} → {info.available}";

        if (!updateStatus)
            return;

        if (info.installed is null)
        {
            item.Status = "Não instalado";
            item.StatusBrush = Brushes.DarkOrange;
        }
        else if (info.available is not null)
        {
            item.Status = "Atualização";
            item.StatusBrush = Brushes.DeepSkyBlue;
        }
        else
        {
            item.Status = "Atualizado";
            item.StatusBrush = Brushes.LimeGreen;
        }
    }

    static int ProgramStatusSortRank(ProgramItem item)
    {
        if (item.InstalledVersion is null)
            return 0; // Não instalado

        if (item.HasUpdate)
            return 1; // Atualização disponível

        return 2; // Atualizado
    }

    void SortProgramsByStatus()
    {
        var ordered = Programs
            .OrderBy(ProgramStatusSortRank)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (int targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var item = ordered[targetIndex];
            int currentIndex = Programs.IndexOf(item);

            if (currentIndex >= 0 && currentIndex != targetIndex)
                Programs.Move(currentIndex, targetIndex);
        }
    }

    void RefreshSelectAllButton()
    {
        int total = Programs.Count;
        int selectedCount = Programs.Count(p => p.Selected);
        int updatesCount = Programs.Count(p => p.HasUpdate);
        int missingCount = Programs.Count(p => p.InstalledVersion is null);

        if (total == 0)
        {
            SelectAllButton.Content = "☑ Todos";
            SelectAllButton.IsEnabled = false;

            SelectUpdatesButton.Content = "☑ Atualizações (0)";
            SelectUpdatesButton.IsEnabled = false;

            SelectMissingButton.Content = "＋ Não instalados (0)";
            SelectMissingButton.IsEnabled = false;
            return;
        }

        bool allSelected = selectedCount == total;

        SelectAllButton.Content = allSelected
            ? "☐ Limpar"
            : "☑ Todos";

        SelectUpdatesButton.Content =
            $"☑ Atualizações ({updatesCount})";

        SelectMissingButton.Content =
            $"＋ Não instalados ({missingCount})";

        bool canSelect =
            _wingetReady &&
            !_wingetBusy &&
            !_versionCheckRunning &&
            _cts is null;

        SelectAllButton.IsEnabled = canSelect;
        SelectUpdatesButton.IsEnabled = canSelect && updatesCount > 0;
        SelectMissingButton.IsEnabled = canSelect && missingCount > 0;
    }

    void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (Programs.Count == 0 || _versionCheckRunning || _cts is not null)
            return;

        bool allSelected = Programs.All(p => p.Selected);
        bool newValue = !allSelected;

        foreach (var item in Programs)
            item.Selected = newValue;

        RefreshSelectAllButton();
        RefreshInstallButton();
        RefreshUpdateAllButton();

        FooterStatus.Text = newValue
            ? $"{Programs.Count} programa(s) selecionado(s)."
            : "Seleção limpa.";
    }

    void SelectUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_versionCheckRunning || _cts is not null)
            return;

        int count = 0;

        foreach (var item in Programs)
        {
            item.Selected = item.HasUpdate;

            if (item.Selected)
                count++;
        }

        RefreshSelectAllButton();
        RefreshInstallButton();
        RefreshUpdateAllButton();

        FooterStatus.Text = count > 0
            ? $"{count} programa(s) com atualização selecionado(s)."
            : "Nenhuma atualização disponível.";
    }

    void SelectMissingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_versionCheckRunning || _cts is not null)
            return;

        int count = 0;

        foreach (var item in Programs)
        {
            item.Selected = item.InstalledVersion is null;

            if (item.Selected)
                count++;
        }

        RefreshSelectAllButton();
        RefreshInstallButton();
        RefreshUpdateAllButton();

        FooterStatus.Text = count > 0
            ? $"{count} programa(s) não instalado(s) selecionado(s)."
            : "Todos os programas da lista já estão instalados.";
    }

    void RefreshInstallButton()
    {
        int selectedMissing = Programs.Count(p =>
            p.Selected &&
            p.InstalledVersion is null);

        InstallButton.IsEnabled =
            _wingetReady &&
            !_wingetBusy &&
            !_versionCheckRunning &&
            _cts is null &&
            selectedMissing > 0;
    }

    void RefreshUpdateAllButton()
    {
        int availableCount = Programs.Count(p => p.HasUpdate);
        int selectedCount = Programs.Count(p => p.HasUpdate && p.Selected);

        if (availableCount == 0)
        {
            UpdateAllButton.Content = "✓ Atualizado";
        }
        else
        {
            UpdateAllButton.Content = $"↻ Atualizar ({selectedCount})";
        }

        UpdateAllButton.IsEnabled =
            _wingetReady &&
            !_wingetBusy &&
            !_versionCheckRunning &&
            _cts is null &&
            selectedCount > 0;
    }

    void RefreshRetryFailuresButton()
    {
        int count = _lastFailedIds.Count;

        RetryFailuresButton.Visibility =
            count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RetryFailuresButton.Content =
            count > 0 ? $"↻ Falhas ({count})" : "↻ Falhas";

        RetryFailuresButton.IsEnabled =
            count > 0 &&
            _wingetReady &&
            !_wingetBusy &&
            !_versionCheckRunning &&
            _cts is null;
    }

    async void RefreshVersionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wingetBusy || _versionCheckRunning || _cts is not null)
            return;

        if (!await EnsureWingetReadyAsync())
        {
            AppendLog("Não foi possível verificar as versões: WinGet não disponível.");
            return;
        }

        AppendLog("Verificando novamente as versões dos programas...");

        await RefreshProgramVersionsAsync();

        int updates = Programs.Count(p => p.HasUpdate);
        int missing = Programs.Count(p => p.InstalledVersion is null);
        int current = Programs.Count - updates - missing;

        AppendLog(
            $"Verificação concluída • {updates} atualização(ões) • " +
            $"{missing} não instalado(s) • {current} atualizado(s).");
    }

    async Task RefreshProgramVersionsAsync(
        IEnumerable<ProgramItem>? targetItems = null)
    {
        var items = (targetItems ?? Programs)
            .Distinct()
            .ToList();

        if (!_wingetReady ||
            _versionCheckRunning ||
            _cts is not null ||
            items.Count == 0)
        {
            RefreshInstallButton();
            RefreshUpdateAllButton();
            return;
        }

        _versionCheckRunning = true;

        InstallButton.IsEnabled = false;
        SelectAllButton.IsEnabled = false;
        SelectUpdatesButton.IsEnabled = false;
        SelectMissingButton.IsEnabled = false;
        UpdateAllButton.IsEnabled = false;
        RetryFailuresButton.IsEnabled = false;
        OpenSearchButton.IsEnabled = false;
        RefreshVersionsButton.IsEnabled = false;
        ProgramsList.IsEnabled = false;

        try
        {
            int total = items.Count;
            int current = 0;

            foreach (var item in items)
            {
                current++;

                item.Status = "Verificando...";
                item.StatusBrush = Brushes.DarkOrange;

                var packageInfo = await GetPackageInfo(item.Id);
                item.ResolvedWingetId = packageInfo.packageId;

                ApplyVersionInfo(
                    item,
                    (packageInfo.installed, packageInfo.available),
                    updateStatus: true);
            }

            SortProgramsByStatus();

            // O resumo inferior foi removido da interface.
        }
        finally
        {
            _versionCheckRunning = false;

            OpenSearchButton.IsEnabled = _wingetReady && _cts is null;
            RefreshVersionsButton.IsEnabled = _wingetReady && _cts is null;
            ProgramsList.IsEnabled = _wingetReady && _cts is null;

            RefreshSelectAllButton();
            RefreshInstallButton();
            RefreshUpdateAllButton();
            RefreshRetryFailuresButton();
        }
    }

    async void UpdateAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureWingetReadyAsync())
            return;

        if (_versionCheckRunning || _cts is not null)
            return;

        var updates = Programs
            .Where(p => p.HasUpdate && p.Selected)
            .ToList();

        if (updates.Count == 0)
        {
            AppDialog.Show(this,
                "Marque pelo menos um programa com atualização disponível.");
            return;
        }

        await RunInstallationAsync(updates, retryMode: false);
    }

    void ProgramCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox &&
            checkBox.Tag is ProgramItem item)
        {
            item.Selected = checkBox.IsChecked == true;
        }

        RefreshSelectAllButton();
        RefreshInstallButton();
        RefreshUpdateAllButton();
    }

    async void RetryFailuresButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureWingetReadyAsync())
            return;

        if (_versionCheckRunning || _cts is not null)
            return;

        var retry = Programs
            .Where(p => _lastFailedIds.Contains(p.Id))
            .ToList();

        if (retry.Count == 0)
        {
            _lastFailedIds.Clear();
            RefreshRetryFailuresButton();
            return;
        }

        foreach (var item in Programs)
            item.Selected = retry.Contains(item);

        await RunInstallationAsync(retry, retryMode: true);
    }

    void StopOverallProgressAnimation()
    {
        try { _progressAnimationCts?.Cancel(); } catch { }
        _progressAnimationCts?.Dispose();
        _progressAnimationCts = null;
    }

    void StartOverallProgressAnimation(int completed, int total)
    {
        // Barra geral removida da interface.
    }

    async Task AnimateOverallProgressAsync(
        double segmentStart,
        double visualLimit,
        double segmentEnd,
        int total,
        CancellationToken token)
    {
        await Task.CompletedTask;
    }

    void CompleteOverallProgressSegment(int completed, int total)
    {
        StopOverallProgressAnimation();
        RefreshProgramCount();
    }

    async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureWingetReadyAsync())
        {
            AppDialog.Show(this,
                "WinGet não está disponível neste Windows.\n\nUse o botão 'Instalar WinGet' antes de instalar os programas.");
            return;
        }

        if (_versionCheckRunning || _cts is not null)
            return;

        var selected = Programs
            .Where(x =>
                x.Selected &&
                x.InstalledVersion is null)
            .ToList();

        if (selected.Count == 0)
        {
            AppDialog.Show(this,
                "Marque pelo menos um programa que ainda não está instalado.");
            return;
        }

        await RunInstallationAsync(selected, retryMode: false);
    }

    async Task RunInstallationAsync(
        List<ProgramItem> selected,
        bool retryMode)
    {
        if (selected.Count == 0)
            return;

        if (!retryMode)
            _lastFailedIds.Clear();

        RefreshRetryFailuresButton();

        InstallButton.IsEnabled = false;
        SelectAllButton.IsEnabled = false;
        SelectUpdatesButton.IsEnabled = false;
        SelectMissingButton.IsEnabled = false;
        UpdateAllButton.IsEnabled = false;
        RetryFailuresButton.IsEnabled = false;
        OpenSearchButton.IsEnabled = false;
        RefreshVersionsButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ProgramsList.IsEnabled = false;

        _cts = new CancellationTokenSource();

        StopOverallProgressAnimation();
        RefreshProgramCount();

        ResetLiveDownloadLog();

        int done = 0;
        int failures = 0;
        int successes = 0;
        int alreadyCurrent = 0;
        var failedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var elapsed = Stopwatch.StartNew();

        LogBox.Clear();
        AppendLog(
            retryMode
                ? $"Repetindo {selected.Count} programa(s) que falharam..."
                : $"Iniciando {selected.Count} programa(s)...");

        foreach (var item in selected)
        {
            if (_cts?.IsCancellationRequested == true)
                break;

            StartOverallProgressAnimation(done, selected.Count);

            item.Status = "Verificando...";
            item.StatusBrush = Brushes.DarkOrange;
            FooterStatus.Text = $"Verificando {item.Name}...";

            var packageInfo = await GetPackageInfo(item.Id);
            item.ResolvedWingetId = packageInfo.packageId;

            string effectiveId = packageInfo.packageId;

            var info = (
                installed: packageInfo.installed,
                available: packageInfo.available);

            ApplyVersionInfo(item, info, updateStatus: false);

            AppendLog($"\n[{done + 1}/{selected.Count}] {item.Name}");
            AppendLog($"ID: {item.Id}");

            if (!effectiveId.Equals(item.Id, StringComparison.OrdinalIgnoreCase))
                AppendLog($"Pacote usado pelo AppInstall: {effectiveId}");

            if (info.installed is not null)
                AppendLog($"Versão instalada: {info.installed}");

            if (info.available is not null)
                AppendLog($"Versão disponível: {info.available}");

            await WaitForMsi(_cts?.Token ?? CancellationToken.None);

            item.Status = "Instalando...";
            item.StatusBrush = Brushes.DodgerBlue;
            FooterStatus.Text = $"Instalando/atualizando {item.Name}...";
            ResetLiveDownloadLog();

            int code;
            string output;

            if (item.Id.Equals(
                "Oracle.JavaRuntimeEnvironment",
                StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("Java 8: usando instalação silenciosa compatível...");

                (code, output) = await RunJava8WithoutInstallerLog(
                    _cts?.Token ?? CancellationToken.None);
            }
            else
            {
                (code, output) = await RunWinget(
                    effectiveId,
                    _cts?.Token ?? CancellationToken.None);
            }

            bool current =
                output.Contains(
                    "Nenhuma atualização disponível",
                    StringComparison.OrdinalIgnoreCase) ||
                output.Contains(
                    "Nenhuma versão de pacote mais recente",
                    StringComparison.OrdinalIgnoreCase) ||
                output.Contains(
                    "No available upgrade found",
                    StringComparison.OrdinalIgnoreCase) ||
                output.Contains(
                    "No newer package versions are available",
                    StringComparison.OrdinalIgnoreCase) ||
                unchecked((uint)code) == 0x8A15002B;

            if (_cts?.IsCancellationRequested == true)
            {
                item.Status = "Cancelado";
                item.StatusBrush = Brushes.DarkGray;
                AppendLog("CANCELADO pelo usuário.");
            }
            else if (code == 0)
            {
                item.Status = "Concluído";
                item.StatusBrush = Brushes.LimeGreen;
                AppendLog("OK: instalado/atualizado com sucesso.");
                successes++;
            }
            else if (current)
            {
                item.Status = "Atualizado";
                item.StatusBrush = Brushes.LimeGreen;
                AppendLog("OK: já está na versão mais recente.");
                alreadyCurrent++;
            }
            else
            {
                item.Status = "Falha";
                item.StatusBrush = Brushes.OrangeRed;
                AppendLog($"FALHA. Código: {code}");

                failures++;
                failedThisRun.Add(item.Id);
            }

            AppendLog("Verificando se a instalação terminou completamente...");
            await WaitForMsi(_cts?.Token ?? CancellationToken.None);

            if (_cts?.IsCancellationRequested != true)
            {
                var afterPackage = await GetPackageInfo(item.Id);
                item.ResolvedWingetId = afterPackage.packageId;

                var after = (
                    installed: afterPackage.installed,
                    available: afterPackage.available);

                ApplyVersionInfo(item, after, updateStatus: false);

                if (after.installed is not null)
                    AppendLog($"Versão após instalação: {after.installed}");
            }

            done++;
            CompleteOverallProgressSegment(done, selected.Count);

            if (_cts?.IsCancellationRequested != true &&
                done < selected.Count)
            {
                await Task.Delay(180);
            }
        }

        elapsed.Stop();

        StopOverallProgressAnimation();
        RefreshProgramCount();

        _lastFailedIds.Clear();
        foreach (var id in failedThisRun)
            _lastFailedIds.Add(id);

        bool canceled = _cts?.IsCancellationRequested == true;

        if (canceled)
        {
            FooterStatus.Text = "Operação cancelada.";
            AppendLog("\nCANCELADO pelo usuário.");
        }
        else
        {
            FooterStatus.Text = failures == 0
                ? "Todos os programas foram processados."
                : $"Concluído com {failures} falha(s).";
        }

        string elapsedText = elapsed.Elapsed.TotalHours >= 1
            ? elapsed.Elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.Elapsed.ToString(@"mm\:ss");

        AppendLog("\nRESUMO DA OPERAÇÃO");
        AppendLog($"✓ Instalados/atualizados: {successes}");
        AppendLog($"✓ Já estavam atualizados: {alreadyCurrent}");
        AppendLog($"✕ Falhas: {failures}");

        if (canceled)
            AppendLog($"■ Não processados/cancelados: {Math.Max(0, selected.Count - done)}");

        AppendLog($"Tempo total: {elapsedText}");

        if (!canceled)
        {
            AppendLog(
                failures == 0
                    ? "CONCLUÍDO: operação finalizada sem falhas."
                    : $"CONCLUÍDO com {failures} falha(s).");
        }

        CancelButton.IsEnabled = false;
        OpenSearchButton.IsEnabled = _wingetReady;
        RefreshVersionsButton.IsEnabled = _wingetReady;
        ProgramsList.IsEnabled = _wingetReady;

        _cts?.Dispose();
        _cts = null;

        SortProgramsByStatus();
        RefreshSelectAllButton();
        RefreshInstallButton();
        RefreshUpdateAllButton();
        RefreshRetryFailuresButton();
    }

    static string DefaultInstallWingetId(string id)
    {
        // Para NOVAS instalações do Chrome, mantemos a variante EXE,
        // que foi validada nas builds anteriores.
        if (id.Equals("Google.Chrome", StringComparison.OrdinalIgnoreCase))
            return "Google.Chrome.EXE";

        return id;
    }

    async Task<(string? installed,string? available)> GetVersionInfoExact(string id)
    {
        var (_,txt)=await RunProcess(
            (ResolveWingetPath() ?? "winget.exe"),
            $"list --id \"{id}\" -e --source winget --accept-source-agreements --disable-interactivity",
            false);

        foreach(var line in txt.Split('\n'))
        {
            int pos=line.IndexOf(id,StringComparison.OrdinalIgnoreCase);
            if(pos<0) continue;

            var tail=line[(pos+id.Length)..].Trim();
            var t=Regex.Split(tail,@"\s+")
                .Where(x=>x.Length>0)
                .ToArray();

            if(t.Length>0)
            {
                string installed = t[0];

                string? available =
                    t.Length>1 &&
                    !t[1].Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                    !t[1].Equals("msstore", StringComparison.OrdinalIgnoreCase)
                        ? t[1]
                        : null;

                return (installed, available);
            }
        }

        return (null,null);
    }

    async Task<(string? installed,string? available,string packageId)> GetPackageInfo(
        string configuredId)
    {
        // O Chrome pode aparecer no WinGet como Google.Chrome (MSI/Enterprise)
        // OU Google.Chrome.EXE. A build 30 consultava somente o EXE e, por isso,
        // um Chrome já instalado pelo outro pacote aparecia como "Não instalado".
        if (configuredId.Equals("Google.Chrome", StringComparison.OrdinalIgnoreCase) ||
            configuredId.Equals("Google.Chrome.EXE", StringComparison.OrdinalIgnoreCase))
        {
            // Primeiro detecta a variante MSI, comum em instalações já existentes.
            var msi = await GetVersionInfoExact("Google.Chrome");
            if (msi.installed is not null)
                return (msi.installed, msi.available, "Google.Chrome");

            // Depois verifica a variante EXE.
            var exe = await GetVersionInfoExact("Google.Chrome.EXE");
            if (exe.installed is not null)
                return (exe.installed, exe.available, "Google.Chrome.EXE");

            // Nenhum instalado: para instalação nova usa a variante EXE,
            // mantendo o comportamento confirmado na build 27.
            return (null, null, "Google.Chrome.EXE");
        }

        string packageId = DefaultInstallWingetId(configuredId);
        var info = await GetVersionInfoExact(packageId);

        return (info.installed, info.available, packageId);
    }

    async Task<(int code,string text)> RunWinget(string id, CancellationToken token) =>
        await RunProcess((ResolveWingetPath() ?? "winget.exe"),
            $"install --id \"{id}\" -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            true, token);

    async Task<(int code,string text)> RunJava8WithoutInstallerLog(CancellationToken token) =>
        await RunProcess((ResolveWingetPath() ?? "winget.exe"),
            "install --id \"Oracle.JavaRuntimeEnvironment\" -e --source winget --silent " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity " +
            "--override \"/s REBOOT=0 SPONSORS=0 AUTO_UPDATE=0\"",
            true, token);

    async Task<(int code,string text)> RunProcess(string file,string args,bool stream, CancellationToken token = default)
    {
        var psi=new ProcessStartInfo(file,args) {
            UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true,
            CreateNoWindow=true, StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8
        };
        using var p=new Process { StartInfo=psi };
        _activeProcess = p;
        var sb=new StringBuilder();
        p.OutputDataReceived += (_,e)=> {
            if(e.Data!=null)
            {
                lock(sb) sb.AppendLine(e.Data);
                if(stream) Dispatcher.Invoke(()=> ProcessWingetStreamLine(e.Data));
            }
        };
        p.ErrorDataReceived += (_,e)=> {
            if(e.Data!=null)
            {
                lock(sb) sb.AppendLine(e.Data);
                if(stream) Dispatcher.Invoke(()=> ProcessWingetStreamLine(e.Data));
            }
        };
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch {}

            if(stream)
            {
                Dispatcher.Invoke(() =>
                {
                    CancelInstallFallbackLog();
                    StopDownloadFallbackLog();
                });
            }

            return (-1, sb.ToString());
        }
        finally
        {
            if (ReferenceEquals(_activeProcess,p))
                _activeProcess=null;
        }

        if(stream)
        {
            Dispatcher.Invoke(() =>
            {
                if (_installFallbackCts is not null)
                    CompleteInstallFallbackLog(p.ExitCode == 0);

                if (_downloadFallbackCts is not null)
                    CompleteDownloadFallbackLog();
            });
        }

        return (p.ExitCode,sb.ToString());
    }

    void ResetLiveDownloadLog()
    {
        StopDownloadFallbackLog();
        StopInstallFallbackLog();
        FinalizeLiveLogLine();
    }

    void ProcessWingetStreamLine(string line)
    {
        bool handled = HandleWingetLiveLine(line);

        if (handled)
            return;

        // "Iniciando a instalação..." vira a própria linha dinâmica,
        // em vez de ser gravada como uma linha estática.
        if (IsWingetInstallStart(line))
        {
            StartInstallFallbackLog();
            return;
        }

        // Quando chega a conclusão da instalação, fixa o tempo decorrido
        // e só depois registra a mensagem original do WinGet.
        if (IsWingetInstallSuccess(line))
            CompleteInstallFallbackLog(success: true);
        else if (IsWingetInstallFailure(line))
            CompleteInstallFallbackLog(success: false);

        AppendLog(line);

        if(IsWingetDownloadStart(line))
            StartDownloadFallbackLog();
    }

    static bool IsWingetInstallStart(string line)
    {
        return line.Contains("Starting package install", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Starting install", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Iniciando a instalação do pacote", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Iniciando instalação do pacote", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Iniciando a instalação", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Iniciando instalação", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsWingetInstallSuccess(string line)
    {
        return line.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Installed successfully", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Instalado com êxito", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Instalado com sucesso", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Instalação concluída com êxito", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Instalação concluída com sucesso", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsWingetInstallFailure(string line)
    {
        return line.Contains("Installer failed", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Installation failed", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Failed to install", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Falha na instalação", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Falha ao instalar", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("O instalador falhou", StringComparison.OrdinalIgnoreCase);
    }

    void StartInstallFallbackLog()
    {
        StopInstallFallbackLog();
        FinalizeLiveLogLine();

        _installFallbackStarted = DateTime.Now;
        _installSpinnerIndex = 0;
        _installFallbackCts = new CancellationTokenSource();

        var token = _installFallbackCts.Token;
        _ = AnimateInstallFallbackAsync(token);
    }

    void StopInstallFallbackLog()
    {
        try { _installFallbackCts?.Cancel(); } catch { }
        _installFallbackCts?.Dispose();
        _installFallbackCts = null;
    }

    string InstallElapsedText(bool includeTenths = false)
    {
        var elapsed = DateTime.Now - _installFallbackStarted;

        if (elapsed.TotalHours >= 1)
            return elapsed.ToString(@"hh\:mm\:ss");

        return includeTenths
            ? $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100}"
            : elapsed.ToString(@"mm\:ss");
    }

    async Task AnimateInstallFallbackAsync(CancellationToken token)
    {
        string[] spinner = ["/", "-", "\\", "|"];

        try
        {
            while (!token.IsCancellationRequested)
            {
                string frame = spinner[_installSpinnerIndex % spinner.Length];
                _installSpinnerIndex++;

                SetLiveLogLine(
                    $"{frame} Iniciando a instalação do pacote... " +
                    $"{InstallElapsedText(includeTenths: true)}");

                await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Esperado quando a instalação termina ou é cancelada.
        }
    }

    void CompleteInstallFallbackLog(bool success)
    {
        if (_installFallbackCts is null)
            return;

        string elapsed = InstallElapsedText();

        StopInstallFallbackLog();

        SetLiveLogLine(
            success
                ? $"✓ Instalação concluída • {elapsed}"
                : $"✕ Instalação encerrada com falha • {elapsed}");

        FinalizeLiveLogLine();
    }

    void CancelInstallFallbackLog()
    {
        if (_installFallbackCts is null)
            return;

        string elapsed = InstallElapsedText();

        StopInstallFallbackLog();
        SetLiveLogLine($"■ Instalação cancelada • {elapsed}");
        FinalizeLiveLogLine();
    }

    static bool IsWingetDownloadStart(string line)
    {
        return line.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Baixando", StringComparison.OrdinalIgnoreCase);
    }

    void StartDownloadFallbackLog()
    {
        StopDownloadFallbackLog();

        _downloadFallbackStarted = DateTime.Now;
        _downloadSpinnerIndex = 0;
        _downloadFallbackCts = new CancellationTokenSource();

        var token = _downloadFallbackCts.Token;
        _ = AnimateDownloadFallbackAsync(token);
    }

    void StopDownloadFallbackLog()
    {
        try { _downloadFallbackCts?.Cancel(); } catch { }
        _downloadFallbackCts?.Dispose();
        _downloadFallbackCts = null;
    }

    string DownloadElapsedText(bool includeTenths = false)
    {
        var elapsed = DateTime.Now - _downloadFallbackStarted;

        if (elapsed.TotalHours >= 1)
            return elapsed.ToString(@"hh\:mm\:ss");

        return includeTenths
            ? $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100}"
            : elapsed.ToString(@"mm\:ss");
    }

    async Task AnimateDownloadFallbackAsync(CancellationToken token)
    {
        string[] spinner = ["/", "-", "\\", "|"];

        try
        {
            while (!token.IsCancellationRequested)
            {
                string frame = spinner[_downloadSpinnerIndex % spinner.Length];
                _downloadSpinnerIndex++;

                SetLiveLogLine(
                    $"{frame} Baixando instalador... {DownloadElapsedText(includeTenths: true)}");

                await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Esperado quando o WinGet fornece progresso real
            // ou quando o download termina.
        }
    }

    void CompleteDownloadFallbackLog()
    {
        if (_downloadFallbackCts is null)
            return;

        string elapsed = DownloadElapsedText();

        StopDownloadFallbackLog();
        SetLiveLogLine($"✓ Download concluído • {elapsed}");
        FinalizeLiveLogLine();
    }

    static string CleanWingetVisualLine(string line)
    {
        return Regex.Replace(
            line ?? "",
            @"\x1B\[[0-?]*[ -/]*[@-~]",
            "").Trim();
    }

    static double DownloadSizeToBytes(double value, string unit)
    {
        return unit.ToUpperInvariant() switch
        {
            "B"  => value,
            "KB" => value * 1024d,
            "MB" => value * 1024d * 1024d,
            "GB" => value * 1024d * 1024d * 1024d,
            "TB" => value * 1024d * 1024d * 1024d * 1024d,
            _ => value
        };
    }

    void SetLiveLogLine(string content)
    {
        if (_liveLogStart < 0)
        {
            _liveLogTime = DateTime.Now.ToString("HH:mm:ss");
            _liveLogStart = LogBox.Text.Length;
        }

        string rendered = $"[{_liveLogTime}] {content}";

        // A linha dinâmica fica SEM quebra de linha enquanto está ativa.
        // Em cada atualização descartamos somente o trecho dinâmico anterior
        // e escrevemos o novo no MESMO ponto do TextBox.
        //
        // Isso evita que / - \\ | ou o tempo 00:01, 00:02, 00:03
        // acabem virando linhas separadas.
        string prefix = _liveLogStart > 0
            ? LogBox.Text[.._liveLogStart]
            : string.Empty;

        LogBox.Text = prefix + rendered;
        LogBox.CaretIndex = LogBox.Text.Length;
        LogBox.ScrollToEnd();
    }

    void FinalizeLiveLogLine()
    {
        if (_liveLogStart < 0)
            return;

        // A quebra de linha só é criada aqui, no momento em que a linha
        // deixa de ser dinâmica. Até então todas as atualizações ocupam
        // exatamente a mesma linha do log.
        if (!LogBox.Text.EndsWith("\r\n", StringComparison.Ordinal))
            LogBox.Text += "\r\n";

        _liveLogStart = -1;
        _liveLogTime = null;
        LogBox.CaretIndex = LogBox.Text.Length;
        LogBox.ScrollToEnd();
    }

    bool HandleWingetLiveLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        string clean = CleanWingetVisualLine(line);

        // Spinner do WinGet: no terminal ele é redesenhado no mesmo lugar.
        // Quando stdout é redirecionado ele chega como várias linhas; ignoramos.
        if (clean is "-" or "\\" or "|" or "/")
            return true;

        // Progresso numérico emitido pelo WinGet moderno:
        // 1024 KB / 822 MB
        // 13.0 MB / 822 MB
        // 811 MB / 822 MB
        var progressMatch = Regex.Match(
            clean,
            @"(?<current>\d+(?:[.,]\d+)?)\s*(?<currentUnit>B|KB|MB|GB|TB)\s*/\s*(?<total>\d+(?:[.,]\d+)?)\s*(?<totalUnit>B|KB|MB|GB|TB)",
            RegexOptions.IgnoreCase);

        if (progressMatch.Success)
        {
            // Se o pacote começou sem porcentagem e depois o WinGet passou
            // a informar bytes/MB, a MESMA linha troca do spinner para a barra real.
            StopDownloadFallbackLog();

            static double ParseNumber(string value) =>
                double.TryParse(
                    value.Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsed)
                    ? parsed
                    : 0d;

            double currentValue = ParseNumber(progressMatch.Groups["current"].Value);
            double totalValue = ParseNumber(progressMatch.Groups["total"].Value);
            string currentUnit = progressMatch.Groups["currentUnit"].Value.ToUpperInvariant();
            string totalUnit = progressMatch.Groups["totalUnit"].Value.ToUpperInvariant();

            double currentBytes = DownloadSizeToBytes(currentValue, currentUnit);
            double totalBytes = DownloadSizeToBytes(totalValue, totalUnit);
            double percent = totalBytes > 0
                ? Math.Clamp(currentBytes / totalBytes * 100d, 0d, 100d)
                : 0d;

            const int width = 30;
            int filled = Math.Clamp((int)Math.Round(percent / 100d * width), 0, width);
            string bar = new string('█', filled) + new string('▒', width - filled);

            SetLiveLogLine(
                $"{bar}  {currentValue:0.#} {currentUnit} / " +
                $"{totalValue:0.#} {totalUnit}  {percent:0}%");

            // Não cria uma nova linha para cada atualização.
            return true;
        }

        // Algumas builds do WinGet podem mandar uma linha só com a barra gráfica.
        // Ela também não deve poluir o log.
        if (Regex.IsMatch(clean, @"^[█▒▓░\s]+$"))
            return true;

        // Quando o download terminou sem o WinGet ter enviado porcentagem,
        // conclui a linha temporizada antes de registrar hash/instalação.
        bool downloadFinished =
            clean.Contains("Successfully verified installer hash", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("Hash do instalador verificado", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("Starting package install", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("Iniciando a instalação", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("Iniciando instalação", StringComparison.OrdinalIgnoreCase);

        if (downloadFinished && _downloadFallbackCts is not null)
        {
            CompleteDownloadFallbackLog();
        }
        else
        {
            StopDownloadFallbackLog();
            FinalizeLiveLogLine();
        }

        return false;
    }

    async Task WaitForMsi(CancellationToken token)
    {
        DateTime stable=DateTime.MinValue, deadline=DateTime.Now.AddMinutes(20);
        while(DateTime.Now<deadline)
        {
            if (token.IsCancellationRequested) return;
            bool busy;
            try { using var m=System.Threading.Mutex.OpenExisting(@"Global\_MSIExecute"); busy=true; }
            catch(System.Threading.WaitHandleCannotBeOpenedException) { busy=false; }
            catch { busy=Process.GetProcessesByName("msiexec").Length>0; }
            if(busy) stable=DateTime.MinValue;
            else {
                if(stable==DateTime.MinValue) stable=DateTime.Now;
                if((DateTime.Now-stable).TotalSeconds>=9) return;
            }
            await Task.Delay(500, token).ContinueWith(_ => { });
        }
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        StopDownloadFallbackLog();
        StopInstallFallbackLog();
        _liveLogStart = -1;
        _liveLogTime = null;
        LogBox.Text = "";
        LogBox.CaretIndex = 0;
    }

    void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;
        StopDownloadFallbackLog();
        CancelInstallFallbackLog();
        FinalizeLiveLogLine();
        FooterStatus.Text = "Cancelando...";
        AppendLog("\nSolicitado cancelamento...");
        _cts.Cancel();
        try { if (_activeProcess is { HasExited:false }) _activeProcess.Kill(true); } catch {}
        CancelButton.IsEnabled=false;
    }

    void AppendLog(string s)
    {
        FinalizeLiveLogLine();

        if (string.IsNullOrWhiteSpace(s))
        {
            LogBox.AppendText("\r\n");
        }
        else
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s.TrimEnd()}\r\n");
        }
        LogBox.ScrollToEnd();
    }
}

public class ProgramItem : INotifyPropertyChanged
{
    bool selected=true;
    bool inJson;
    string versionText="Aguardando";
    string status="Pendente";
    Brush statusBrush=Brushes.Gray;

    public string Name {get;set;}="";
    public string Id {get;set;}="";

    public string? InstalledVersion { get; set; }
    public string? AvailableVersion { get; set; }
    public bool HasUpdate { get; set; }

    // Pode ser diferente do ID salvo no programas.json.
    // Ex.: Google.Chrome ou Google.Chrome.EXE.
    public string? ResolvedWingetId { get; set; }

    public bool Selected
    {
        get=>selected;
        set{selected=value;On(nameof(Selected));}
    }

    public bool InJson
    {
        get=>inJson;
        set
        {
            if(inJson==value) return;
            inJson=value;
            On(nameof(InJson));
            On(nameof(JsonButtonVisibility));
        }
    }

    public Visibility JsonButtonVisibility =>
        InJson ? Visibility.Visible : Visibility.Collapsed;

    public string VersionText
    {
        get=>versionText;
        set{versionText=value;On(nameof(VersionText));}
    }

    public string Status
    {
        get=>status;
        set{status=value;On(nameof(Status));}
    }

    public Brush StatusBrush
    {
        get=>statusBrush;
        set{statusBrush=value;On(nameof(StatusBrush));}
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void On(string n)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(n));
}
public class Config { public List<ProgramCfg> programas {get;set;}=[]; }
public class ProgramCfg { public string nome {get;set;}=""; public string id {get;set;}=""; }

