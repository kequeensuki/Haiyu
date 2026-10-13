using Haiyu.Common.Contracts;
using Waves.Api.Models.CloudGame;

namespace Haiyu.Pages.Dialogs;

public sealed partial class CloudAndroidCredentialDialog : ContentDialog, IDialog
{
    public CloudAndroidCredentialDialog(
        CloudAndroidCredentialViewModel viewModel,
        IThemeService themeService
    )
    {
        InitializeComponent();
        ViewModel = viewModel;
        RequestedTheme = themeService.CurrentTheme;
    }

    public CloudAndroidCredentialViewModel ViewModel { get; }

    public void SetData(object data)
    {
        if (data is CloudGameLoginData user)
        {
            ViewModel.SetUser(user);
        }
    }
}
