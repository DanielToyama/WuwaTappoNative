namespace WuwatappoTokenExtractor;

/// <summary>集中维护用户可见的安全提示文案，避免两个入口出现不一致。</summary>
public static class TokenSafety
{
    /// <summary>成功取得 token 后必须展示的登录凭据警告。</summary>
    public const string CredentialWarning =
        "这是你的登录凭据，请确保输入到任何地方的时候，都代表你信任此渠道。" +
        "请不要在任何情况下公开展示此内容。" +
        "如果已泄露，可以通过重新通过在官方/改版中使用手机号/游戏uid重新登陆来更新token。";
}
