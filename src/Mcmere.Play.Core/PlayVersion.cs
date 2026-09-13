namespace Mcmere.Play.Core;
public static class PlayVersion
{
    public static string Current => typeof(PlayVersion).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
