using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace WingetInstaller;

public partial class SearchWindow : Window
{
    public ObservableCollection<SearchResultItem> Results { get; } = new();
    public List<SearchResultItem> SelectedPrograms { get; } = new();
    public bool SavedToJson { get; private set; }
    public int SavedToJsonCount { get; private set; }

    // Mantém as seleções mesmo quando uma nova pesquisa substitui a lista visível.
    readonly Dictionary<string, SearchResultItem> _pendingSelections =
        new(StringComparer.OrdinalIgnoreCase);

    readonly string _locale = CultureInfo.CurrentUICulture.Name;

    public SearchWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = Results;
        LocaleText.Text = $"Idioma do Windows: {_locale} • resultados localizados são priorizados";
        SearchBox.Focus();
    }

    async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchWinget();

    async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchWinget();
        }
    }

    async Task SearchWinget()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length < 2)
        {
            AppDialog.Show(this, "Digite pelo menos 2 caracteres para pesquisar.");
            return;
        }

        SearchButton.IsEnabled = false;
        SearchBox.IsEnabled = false;

        SaveCurrentSelections();
        Results.Clear();

        // Toda nova pesquisa começa no topo da lista.
        ResultsScroll.ScrollToTop();

        StatusText.Text = $"Pesquisando '{query}'...";

        try
        {
            // O `winget search --query` sozinho nem sempre devolve todos os
            // resultados esperados para termos amplos. Ex.: pesquisar "adobe"
            // pode não trazer o Acrobat Reader, embora "reader" traga.
            //
            // Fazemos buscas complementares por consulta, nome e ID, unimos tudo
            // por Package ID e ranqueamos localmente pela relevância.
            var parsed = await SearchWingetBroadly(query);

            // Em pesquisas parciais o winget às vezes retorna apenas o pacote genérico.
            // A checagem abaixo é genérica: para cada resultado, procuramos uma variante
            // correspondente à localidade do Windows (ex.: .pt-BR), se ela existir.
            parsed = await AddSystemLocaleVariants(parsed);

            var filtered = PreferSystemLocale(parsed, _locale)
                .OrderBy(x => SearchRank(x, query))
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(100)
                .ToList();

            foreach (var item in filtered)
            {
                if (_pendingSelections.ContainsKey(item.Id))
                    item.Selected = true;

                item.PropertyChanged += (_, __) =>
                {
                    if (item.Selected)
                        _pendingSelections[item.Id] = CloneItem(item);
                    else
                        _pendingSelections.Remove(item.Id);

                    UpdateAddButton();
                };

                Results.Add(item);
            }

            UpdateAddButton();

            // Garante que uma lista grande nunca reaproveite a posição de rolagem
            // da pesquisa anterior.
            ResultsScroll.ScrollToTop();
            await Dispatcher.InvokeAsync(
                () => ResultsScroll.ScrollToTop(),
                System.Windows.Threading.DispatcherPriority.Loaded);

            StatusText.Text = Results.Count == 0
                ? $"Nenhum resultado para '{query}'."
                : $"{Results.Count} resultado(s). Selecione os programas que deseja adicionar.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "WinGet não está disponível. Feche esta janela e instale o WinGet na tela principal.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao pesquisar: {ex.Message}";
        }
        finally
        {
            SearchButton.IsEnabled = true;
            SearchBox.IsEnabled = true;
            SearchBox.Focus();
        }
    }

    async Task<List<SearchResultItem>> SearchWingetBroadly(string query)
    {
        string safe = query.Replace("\"", "").Trim();
        var merged = new Dictionary<string, SearchResultItem>(
            StringComparer.OrdinalIgnoreCase);

        async Task AddSearch(string option, string value)
        {
            var (_, output) = await RunProcess(
                WingetExePath(),
                $"search {option} \"{value}\" --source winget --count 1000 " +
                "--accept-source-agreements --disable-interactivity");

            foreach (var item in ParseWingetSearch(output))
            {
                if (!merged.TryGetValue(item.Id, out var existing))
                {
                    merged[item.Id] = item;
                    continue;
                }

                // Mantém a entrada mais completa caso uma das consultas retorne
                // nome/versão melhor formatados.
                if (item.Name.Length > existing.Name.Length)
                    existing.Name = item.Name;

                if (!string.IsNullOrWhiteSpace(item.Version))
                    existing.Version = item.Version;
            }
        }

        // 1) busca geral do WinGet
        await AddSearch("--query", safe);

        // 2) busca explícita pelo nome: importante para fabricantes/marcas
        //    como Adobe, Microsoft, Google etc.
        await AddSearch("--name", safe);

        // 3) busca pelo ID cobre casos em que o nome exibido é diferente.
        await AddSearch("--id", safe);

        // Para consultas com várias palavras, também consulta cada termo
        // individualmente. Depois o ranqueamento coloca no topo os resultados
        // que contêm TODOS os termos.
        var terms = SearchTerms(safe);
        if (terms.Count > 1)
        {
            foreach (var term in terms.Where(t => t.Length >= 3).Distinct(
                StringComparer.OrdinalIgnoreCase))
            {
                await AddSearch("--query", term);
            }
        }

        return merged.Values.ToList();
    }

    static List<string> SearchTerms(string query) =>
        Regex.Matches(query, @"[\p{L}\p{N}]+")
            .Select(m => m.Value)
            .Where(x => x.Length >= 2)
            .ToList();

    static int SearchRank(SearchResultItem item, string query)
    {
        string q = query.Trim();
        string name = item.Name ?? "";
        string id = item.Id ?? "";

        if (name.Equals(q, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(q, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (id.Contains(q, StringComparison.OrdinalIgnoreCase))
            return 3;

        var terms = SearchTerms(q);
        if (terms.Count > 0)
        {
            bool allName = terms.All(t =>
                name.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (allName) return 4;

            bool allCombined = terms.All(t =>
                name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                id.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (allCombined) return 5;

            int matched = terms.Count(t =>
                name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                id.Contains(t, StringComparison.OrdinalIgnoreCase));

            if (matched > 0)
                return 10 - Math.Min(4, matched);
        }

        return 20;
    }

    async Task<List<SearchResultItem>> AddSystemLocaleVariants(
        List<SearchResultItem> items)
    {
        if (string.IsNullOrWhiteSpace(_locale) || items.Count == 0)
            return items;

        static string LastPart(string id) =>
            id.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

        static string BaseWithoutSuffix(string id)
        {
            int p = id.LastIndexOf('.');
            return p > 0 ? id[..p] : id;
        }

        static bool LooksLikeLocaleSuffix(string suffix)
        {
            // pt-BR, en-US, de-DE etc.
            if (Regex.IsMatch(suffix, @"(?i)^[a-z]{2,3}-[a-z]{2}$"))
                return true;

            // Idiomas curtos sem região, como pt, en, fr.
            // Não tratamos qualquer sigla de 3 letras como localidade para
            // evitar falsos positivos em IDs como VideoLAN.VLC.
            return Regex.IsMatch(suffix, @"(?i)^[a-z]{2}$");
        }

        var existingIds = items
            .Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Monta os IDs-base de TODOS os resultados atuais.
        // Não depende mais de "firef", "chrome", "vlc" etc.
        var baseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            string id = item.Id;
            string suffix = LastPart(id);

            if (suffix.Equals(_locale, StringComparison.OrdinalIgnoreCase))
                continue;

            if (LooksLikeLocaleSuffix(suffix))
                baseIds.Add(BaseWithoutSuffix(id));
            else
                baseIds.Add(id);
        }

        try
        {
            // Uma única consulta ao catálogo por IDs que contêm a localidade do Windows.
            // Assim evitamos executar dezenas de "winget search" individuais.
            var (_, localeText) = await RunProcess(
                "winget.exe",
                $"search --id \"{_locale}\" --source winget --count 1000 --accept-source-agreements --disable-interactivity");

            var localeItems = ParseWingetSearch(localeText).ToList();

            foreach (var baseId in baseIds)
            {
                string wantedId = $"{baseId}.{_locale}";

                if (existingIds.Contains(wantedId))
                    continue;

                var localized = localeItems.FirstOrDefault(x =>
                    x.Id.Equals(wantedId, StringComparison.OrdinalIgnoreCase));

                if (localized is not null)
                {
                    items.Add(localized);
                    existingIds.Add(localized.Id);
                }
            }
        }
        catch
        {
            // A pesquisa principal continua válida mesmo se a checagem global
            // de variantes localizadas falhar.
        }

        return items;
    }

    static IEnumerable<SearchResultItem> PreferSystemLocale(List<SearchResultItem> items, string systemLocale)
    {
        var sys = systemLocale.ToLowerInvariant();
        var lang = sys.Split('-')[0];

        static string LastPart(string id) =>
            id.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

        static string BaseWithoutSuffix(string id)
        {
            int p = id.LastIndexOf('.');
            return p > 0 ? id[..p] : id;
        }

        static bool LooksLikeLocale(string suffix) =>
            Regex.IsMatch(suffix, @"(?i)^[a-z]{2,3}(?:-[a-z]{2})?$");

        // Famílias claramente localizadas, como Mozilla.Firefox.pt-BR/.ast/.fur.
        var localizedBases = items
            .Where(x => LooksLikeLocale(LastPart(x.Id)))
            .GroupBy(x => BaseWithoutSuffix(x.Id), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var exactLocalizedBases = items
            .Where(x =>
            {
                var suffix = LastPart(x.Id).ToLowerInvariant();
                return suffix == sys || suffix == lang;
            })
            .Select(x => BaseWithoutSuffix(x.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var suffix = LastPart(item.Id).ToLowerInvariant();
            var baseId = BaseWithoutSuffix(item.Id);

            if (localizedBases.Contains(baseId))
            {
                if (suffix == sys || suffix == lang)
                    yield return item;
                continue;
            }

            // Se existe "Produto.pt-BR", oculta o pacote genérico da mesma família
            // quando ele é a edição padrão em inglês. Mantém edições distintas (MSIX etc.).
            if (exactLocalizedBases.Contains(item.Id))
                continue;

            yield return item;
        }
    }

    static IEnumerable<SearchResultItem> ParseWingetSearch(string text)
    {
        var lines = text
            .Replace("\r", "")
            .Split('\n');

        bool afterSeparator = false;

        foreach (var raw in lines)
        {
            string line = StripAnsi(raw).TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!afterSeparator)
            {
                // O separador do winget pode ser uma linha contínua de traços
                // ou vários blocos de traços. Não dependemos do número de colunas.
                string trimmed = line.Trim();
                if (Regex.IsMatch(trimmed, @"^(?:-{3,}\s*)+$"))
                    afterSeparator = true;

                continue;
            }

            // Primeiro tenta localizar o Package ID diretamente na linha.
            // IDs do catálogo winget seguem normalmente o padrão Publisher.Product
            // e não possuem espaços. Isso é mais confiável do que usar a largura
            // visual das colunas, que muda conforme idioma/consulta/tamanho do nome.
            var idMatches = Regex.Matches(
                line,
                @"(?<!\S)(?<id>[A-Za-z0-9][A-Za-z0-9_+.-]*\.[A-Za-z0-9][A-Za-z0-9_+.-]*)(?!\S)");

            SearchResultItem? parsed = null;

            foreach (Match idMatch in idMatches.Cast<Match>())
            {
                string name = line[..idMatch.Index].Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string id = idMatch.Groups["id"].Value.Trim();

                string tail = line[(idMatch.Index + idMatch.Length)..].Trim();
                if (string.IsNullOrWhiteSpace(tail))
                    continue;

                // A primeira palavra depois do ID é a versão.
                // O restante pode ser Match/Correspondência/Fonte e é ignorado.
                var versionMatch = Regex.Match(tail, @"^(?<version>\S+)");
                if (!versionMatch.Success)
                    continue;

                string version = versionMatch.Groups["version"].Value.Trim();

                // Evita falsos positivos óbvios.
                if (Regex.IsMatch(id, @"^\d+(?:\.\d+)+$"))
                    continue;

                parsed = new SearchResultItem
                {
                    Name = name,
                    Id = id,
                    Version = version
                };
                break;
            }

            if (parsed is not null)
            {
                yield return parsed;
                continue;
            }

            // Fallback para casos raros em que o ID não contenha ponto.
            // Mantém compatibilidade com a lógica antiga, mas só é usado quando
            // o parser principal acima não conseguiu identificar o pacote.
            var cols = Regex.Split(line.Trim(), @"\s{2,}")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            if (cols.Length < 3)
                continue;

            int idIndex = Array.FindIndex(
                cols,
                1,
                x => !x.Contains(' ') &&
                     !Regex.IsMatch(x, @"^\d+(?:\.\d+)+$") &&
                     (x.Contains('.') || x.Contains('-')));

            if (idIndex < 1 || idIndex >= cols.Length - 1)
                continue;

            string fallbackName = string.Join(" ", cols.Take(idIndex)).Trim();
            string fallbackId = cols[idIndex].Trim();
            string fallbackVersion = cols[idIndex + 1].Trim();

            if (!string.IsNullOrWhiteSpace(fallbackName) &&
                !string.IsNullOrWhiteSpace(fallbackId))
            {
                yield return new SearchResultItem
                {
                    Name = fallbackName,
                    Id = fallbackId,
                    Version = fallbackVersion
                };
            }
        }
    }

    static string StripAnsi(string value) =>
        Regex.Replace(value ?? "", @"\x1B\[[0-?]*[ -/]*[@-~]", "");


    void SaveCurrentSelections()
    {
        foreach (var item in Results)
        {
            if (item.Selected)
                _pendingSelections[item.Id] = CloneItem(item);
            else
                _pendingSelections.Remove(item.Id);
        }
    }

    static SearchResultItem CloneItem(SearchResultItem item) => new()
    {
        Name = item.Name,
        Id = item.Id,
        Version = item.Version,
        Selected = true
    };

    void UpdateAddButton()
    {
        int count = _pendingSelections.Count;
        bool enabled = count > 0 || Results.Any(x => x.Selected);

        AddButton.IsEnabled = enabled;
        SaveJsonButton.IsEnabled = enabled;

        AddButton.Content = count > 0
            ? $"Adicionar selecionados ({count})"
            : "Adicionar selecionados";

        SaveJsonButton.Content = count > 0
            ? $"Adicionar ao programas.json ({count})"
            : "Adicionar ao programas.json";
    }

    void AddButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentSelections();

        SelectedPrograms.Clear();
        SelectedPrograms.AddRange(_pendingSelections.Values.Select(CloneItem));

        if (SelectedPrograms.Count == 0) return;
        DialogResult = true;
        Close();
    }

    void SaveJsonButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentSelections();

        SelectedPrograms.Clear();
        SelectedPrograms.AddRange(_pendingSelections.Values.Select(CloneItem));
        if (SelectedPrograms.Count == 0) return;

        try
        {
            SavedToJsonCount = SaveSelectedProgramsToJson(SelectedPrograms);
            SavedToJson = true;

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SavedToJson = false;
            SavedToJsonCount = 0;
            AppDialog.Show(this, $"Não foi possível atualizar programas.json.\n\n{ex.Message}");
        }
    }

    static int SaveSelectedProgramsToJson(IEnumerable<SearchResultItem> selected)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "programas.json");
        Config cfg;

        if (File.Exists(path))
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            cfg = JsonSerializer.Deserialize<Config>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new Config();
        }
        else
        {
            cfg = new Config();
        }

        cfg.programas ??= new List<ProgramCfg>();

        int added = 0;
        foreach (var item in selected)
        {
            bool exists = cfg.programas.Any(p =>
                p.id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));

            if (exists) continue;

            cfg.programas.Add(new ProgramCfg
            {
                nome = item.Name,
                id = item.Id
            });
            added++;
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        string output = JsonSerializer.Serialize(cfg, options);
        File.WriteAllText(path, output + Environment.NewLine, new UTF8Encoding(false));

        return added;
    }

    void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    static string WingetExePath()
    {
        string localAlias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");

        return File.Exists(localAlias) ? localAlias : "winget.exe";
    }

    static async Task<(int code, string text)> RunProcess(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return (p.ExitCode, sb.ToString());
    }
}

public class SearchResultItem : INotifyPropertyChanged
{
    bool _selected;
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Selected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}