namespace AutoPickup.Core.Menus;

/// <summary>暂停菜单导航表：只记“顺序”，不记“绝对位置”。层级路径语义与原 gtaz commons 对齐。</summary>
public static class MenuRegistry
{
    // 在线模式（Pause 打开后各 tab 的顶层列表项，自上而下）
    public static readonly string[] OnlineTabNames = { "地图", "在线", "职业", "好友", "信息", "商店", "设置", "统计", "相册" };
    public static readonly string[] StoryTabNames = { "地图", "简讯", "统计", "设置", "游戏", "在线", "好友", "相册", "商店", "Rockstar编辑器" };

    public static readonly string[] OnlineMainItems =
    {
        "差事", "加入好友", "加入帮会成员", "游玩清单", "玩家", "帮会", "Rockstar制作器",
        "管理角色", "迁移档案", "GTA加会员", "购买鲨鱼现金卡", "安全与提示", "选项",
        "寻找新战局", "制作人员名单和法律声明", "退至故事模式", "退至主菜单", "退出游戏",
    };

    public static readonly string[] OnlineFindNewSessionItems =
    {
        "公开战局", "仅限邀请的战局", "帮会战局", "非公开帮会战局", "非公开好友战局",
    };

    public static readonly string[] StoryOnlineTabItems =
    {
        "加入好友", "加入帮会成员", "帮会", "Rockstar制作器", "进入GTA在线模式",
    };

    public static readonly string[] StoryJoinOnlineItems =
    {
        "进入", "凭邀请加入的战局", "帮会战局", "非公开帮会战局", "非公开好友战局",
    };

    public static int IndexOf(string[] list, string normalized)
    {
        for (int i = 0; i < list.Length; i++)
        {
            if (TextMatcher.FuzzyEqual(normalized, list[i])) return i;
        }
        return -1;
    }
}
