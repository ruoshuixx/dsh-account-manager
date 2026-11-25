using AccountManager.App.Services;
using AccountManager.Core.Actions;
using AccountManager.Core.Dsh;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AccountManager.App.Views;

/// <summary>Editor for one API provider: name, protocol, baseURL, models and optional key rotation.</summary>
public partial class ApiConfigDialog : Window
{
    private ApiConfigInput? _input;
    private CancellationTokenSource? _query;

    public ApiConfigDialog()
    {
        InitializeComponent();
        NameBox.ItemsSource = ApiProviderTemplates.Names;
        NameBox.SelectionChanged += OnTemplatePicked;
        KeyBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) { UpdateQueryButton(); } };
        Closed += (_, _) => _query?.Cancel();
        // Property notifications are synchronous (the TextChanged event is not), so the hints never lag behind the fields.
        BaseUrlBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) { RefreshHints(); } };
        ResponsesRadio.IsCheckedChanged += (_, _) => RefreshHints();
        DialogWindowBounds.KeepOnScreen(this);
    }

    // True while the dialog is being filled from the stored provider: nothing may be applied then.
    private bool _loading;

    public ApiConfigResult? Result { get; private set; }

    public static async Task<ApiConfigResult?> ShowAsync(Window owner, ApiConfigInput input)
    {
        var dialog = new ApiConfigDialog { _input = input };
        dialog.HeaderText.Text = "配置供应商：" + input.Title;
        dialog.ProviderText.Text = "供应商 ID：" + input.ProviderId + "（保存时会写入 dsh 的供应商配置）";
        dialog._loading = true;
        dialog.NameBox.Text = string.IsNullOrWhiteSpace(input.DisplayName) ? input.Title : input.DisplayName;
        dialog.BaseUrlBox.Text = input.BaseUrl == "地址未配置" ? string.Empty : input.BaseUrl;
        dialog.ModelsBox.Text = string.Join(Environment.NewLine, input.Models);
        dialog.ResponsesRadio.IsChecked = input.UseResponses;
        dialog.ChatRadio.IsChecked = !input.UseResponses;
        dialog.FastBox.IsChecked = input.HasFast;
        dialog.RefreshHints();
        dialog._loading = false;
        dialog.KeyBox.IsEnabled = true;
        dialog.KeyBox.Watermark = input.HasCredential ? "留空保留当前密钥" : "该行没有本地密钥，可在此新建";
        dialog.HintText.Text = input.HasCredential
            ? "留空密钥将保留现有密钥；填写新密钥会写入 dsh 的凭据文件并替换旧密钥。"
            : "当前只有供应商配置，没有本地密钥；填写密钥会写入 dsh 的凭据文件。";
        DialogWindowBounds.Fit(dialog, owner, center: true);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    /// <summary>Picking a template (not typing) fills the empty fields; see <see cref="ApiProviderTemplates.Apply"/>.</summary>
    private void OnTemplatePicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || e.AddedItems.Count == 0 || ApiProviderTemplates.Find(e.AddedItems[0] as string) is not { } template) { return; }
        var applied = ApiProviderTemplates.Apply(template, BaseUrlBox.Text, ModelsBox.Text, ResponsesRadio.IsChecked == true);
        NameBox.Text = applied.Name;
        BaseUrlBox.Text = applied.BaseUrl;
        ModelsBox.Text = applied.Models;
        ResponsesRadio.IsChecked = applied.UseResponses;
        ChatRadio.IsChecked = !applied.UseResponses;
        RefreshHints();
    }

    private bool FastOffered => ApiFastTwins.Qualifies(BaseUrlBox.Text?.Trim(), ResponsesRadio.IsChecked == true);

    /// <summary>The auto-configuration hint under the models box and the Fast tick follow the address and protocol.</summary>
    private void RefreshHints()
    {
        UpdateQueryButton();
        AutoConfigText.Text = ApiProviderTemplates.AutoConfigHint(BaseUrlBox.Text?.Trim(), ResponsesRadio.IsChecked == true);
        AutoConfigText.IsVisible = !string.IsNullOrEmpty(AutoConfigText.Text);
        FastBox.IsVisible = FastOffered;
    }

    private void UpdateQueryButton()
    {
        var target = OfficialModelList.MatchBase(BaseUrlBox.Text);
        LatestGptBox.IsVisible = OpenAiModelFilter.AppliesTo(target);
        QueryButton.IsEnabled = _input?.QueryModelsAsync is not null && target is not null &&
            (!string.IsNullOrWhiteSpace(KeyBox.Text) || (_input.HasCredential && OfficialModelList.MatchBase(_input.BaseUrl) == target));
    }

    private async void OnQuery(object? sender, RoutedEventArgs e)
    {
        if (_query is not null || _input?.QueryModelsAsync is not { } queryModels) { return; }
        using var query = new CancellationTokenSource();
        _query = query;
        FormPanel.IsEnabled = SaveButton.IsEnabled = false;
        QueryStatus.Text = "正在查询官方模型列表 …";
        ErrorText.Text = string.Empty;
        try
        {
            var result = await queryModels(BaseUrlBox.Text ?? string.Empty, KeyBox.Text ?? string.Empty, LatestGptBox.IsChecked == true, query.Token);
            if (query.IsCancellationRequested) { return; }
            ModelsBox.Text = string.Join(Environment.NewLine, result.Models);
            QueryStatus.Text = $"{result.Source}：已填入 {result.Models.Count} 个模型。保存将移除未保留的旧条目。";
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested) { }
        catch (DshException ex) { QueryStatus.Text = "模型列表未更改。"; ErrorText.Text = ex.Message; }
        catch (Exception) { QueryStatus.Text = "查询失败，模型列表未更改。"; }
        finally { FormPanel.IsEnabled = SaveButton.IsEnabled = true; _query = null; }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            Result = new ApiConfigResult
            {
                DisplayName = ApiProviderValidation.Name(NameBox.Text),
                BaseUrl = ApiProviderValidation.Endpoint(BaseUrlBox.Text),
                Models = ApiProviderValidation.ParseModels(ModelsBox.Text),
                UseResponses = ResponsesRadio.IsChecked == true,
                Fast = FastOffered && FastBox.IsChecked == true,
                NewKey = ApiProviderValidation.Key(KeyBox.Text, required: false),
            };
            KeyBox.Text = string.Empty;
            Close();
        }
        catch (DshException ex) { ErrorText.Text = ex.Message; }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
