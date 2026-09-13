using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public static class RuntimeCatalog
{
    public static readonly RuntimeArtifact Prism = new("prism-11.1.0-win-x64", "11.1.0",
        "https://github.com/PrismLauncher/PrismLauncher/releases/download/11.1.0/PrismLauncher-Windows-MinGW-w64-Portable-11.1.0.zip",
        "2bf5e879ea1c3f6a1aaaa43539667ce296308abf3e6a984d5cc4c48bfe3c431c", 43926838, "prismlauncher.exe",
        "https://prismlauncher.org/wiki/overview/copying/");
    public static readonly RuntimeArtifact Java21 = new("temurin-21-d35f31e712f0", "21.0.12.1+1-LTS",
        "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/OpenJDK21U-jre_x64_windows_hotspot_21.0.12.1_1.zip",
        "d35f31e712f0fcf6ac5a093edc90204fbff22f720ba3950bd09d331d5e621636", 48999141, "jdk-21.0.12.1+1-jre/bin/java.exe",
        "https://openjdk.org/legal/gplv2+ce.html");
    public static RuntimeArtifact Java(JavaRequirement requirement)
    {
        if (requirement.Major != 21 || requirement.Architecture != "x64" || requirement.RuntimeCatalogId != Java21.Id)
            throw new DistributionException("runtime_catalog_required", "このJava構成に対応するアプリの更新が必要です。");
        return Java21;
    }
}
