namespace Haiyu.ABI.Services.GameDataExport.Gacha;

/// <summary>
/// 官方抽卡记录页面可查询的池类型与显示名。
/// <para>
/// <b>出处</b>：生产包 <c>aki/gacha/assets/index-e63ca197.js</c> 的常量 <c>C</c>，以及
/// <c>aki/gacha/locales/zh-hans.json</c> 的 <c>selectList</c>。
/// 与本仓库既有的 <c>CardPoolType</c>（1..11）不同：页面没有 7/10/11，但独有 12/13。
/// </para>
/// <para>
/// 接口返回的 <c>cardPoolType</c> 字段是**本地化显示文本**，实测 8/9/12/13 只返回裸数字，
/// 不能用于分类或展示。因此分类与标签都以**请求时的池编号**为准，使用本表。
/// </para>
/// </summary>
internal static class GachaPoolTypes
{
    /// <summary>官方页面可查询的全部池类型（顺序与页面下拉一致）。</summary>
    internal static readonly int[] All = [1, 2, 3, 4, 5, 6, 8, 9, 12, 13];

    /// <summary>
    /// 池编号 → 官方简体中文名。来自页面 <c>selectList</c>，与游戏内唤取界面文案一致。
    /// 未收录的编号返回 <c>null</c>，由调用方决定显示为"未本地化池(n)"，不猜测。
    /// </summary>
    internal static string? Label(int cardPoolType) => cardPoolType switch
    {
        1 => "角色活动唤取",
        2 => "武器活动唤取",
        3 => "角色常驻唤取",
        4 => "武器常驻唤取",
        5 => "新手唤取",
        6 => "新手自选唤取",
        8 => "角色新旅唤取",
        9 => "武器新旅唤取",
        12 => "角色忆旅唤取",
        13 => "武器忆旅唤取",
        _ => null,
    };

    /// <summary>展示用标签：已知池用官方名，未知池显式标注，不做推测。</summary>
    internal static string DisplayLabel(int cardPoolType) =>
        Label(cardPoolType) ?? string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"未本地化池({cardPoolType})");
}
