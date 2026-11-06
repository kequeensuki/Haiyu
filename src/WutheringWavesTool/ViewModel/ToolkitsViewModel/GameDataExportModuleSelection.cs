namespace Haiyu.ViewModel.ToolkitsViewModel;

public sealed partial class GameDataExportModuleSelection(string id, string name, bool selected) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    [ObservableProperty]
    public partial bool IsSelected { get; set; } = selected;
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;
}
