using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Attempts to turn off the "Made with Unity" splash for Android builds.
// Unity's Personal-tier license historically forced this on; that
// restriction was lifted starting with the Unity 2023 LTS / "Unity 6"
// generation (the splash became fully optional regardless of tier). This
// project is on 6000.6.2f1, which is past that change - but rather than
// assume the license in this specific installation actually allows it,
// this reads PlayerSettings.SplashScreen back AFTER attempting the change
// and logs the real resulting values, so "did it actually take" is
// verified instead of guessed.
public static class V07SplashScreenConfig
{
    [MenuItem("Freehand/V0.7/1 - Disable Made With Unity Splash")]
    public static void Disable()
    {
        var log = new StringBuilder();

        log.AppendLine($"Antes: show={PlayerSettings.SplashScreen.show} showUnityLogo={PlayerSettings.SplashScreen.showUnityLogo}");

        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;

        AssetDatabase.SaveAssets();

        var showAfter = PlayerSettings.SplashScreen.show;
        var logoAfter = PlayerSettings.SplashScreen.showUnityLogo;
        log.AppendLine($"Depois: show={showAfter} showUnityLogo={logoAfter}");

        if (!showAfter && !logoAfter)
            log.AppendLine("RESULTADO: splash desativado com sucesso - o app deve pular direto para a cena no boot.");
        else
            log.AppendLine("RESULTADO: um ou ambos os valores voltaram para true depois de tentar desativar - " +
                "provável restrição de licenciamento desta instalação do Unity. Não foi criado nenhum hack de APK " +
                "para forçar isso; o valor real ficou registrado acima em vez de assumido.");

        var outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "V07SplashScreenConfig.log");
        File.WriteAllText(outPath, log.ToString());
        Debug.Log($"[V07SplashScreenConfig] {(!showAfter && !logoAfter ? "OK" : "PARCIAL")} - ver {outPath}");
    }
}
