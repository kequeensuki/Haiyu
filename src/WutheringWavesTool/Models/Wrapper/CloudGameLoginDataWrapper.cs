using Waves.Api.Models.CloudGame;

namespace Haiyu.Models.Wrapper;


public partial class CloudGameLoginDataWrapper:ObservableObject
{
    [ObservableProperty]
    public partial string Id { get; set; }

    [ObservableProperty]
    public partial string Phone { get; set; }


    [ObservableProperty]
    public partial bool IsSelect { get; set; }

    [ObservableProperty]
    public partial string UserName { get; set; }

    /// <summary>
    /// 是否已配置安卓版云鸣潮凭据（会话将以安卓版身份建立）。
    /// </summary>
    [ObservableProperty]
    public partial bool HasAndroidCredential { get; set; }

    [RelayCommand]
    public void Delete()
    {
        WeakReferenceMessenger.Default.Send<DeleteCloudUserMessager>(new(this.Id));
    }
    public CloudGameLoginDataWrapper(CloudGameLoginData data)
    {
        this.Id = data.GetId();
        this.Phone = data.Phone;
        this.UserName = data.Username;
        this.HasAndroidCredential = data.HasAndroidCredentials;
    }
}
