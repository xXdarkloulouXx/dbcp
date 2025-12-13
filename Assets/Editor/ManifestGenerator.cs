using UnityEngine;
using UnityEditor; // Important: ce script utilise des fonctions de l'éditeur
using System.IO;
using System.Linq;
using System.Text;

public class ManifestGenerator
{
    // Crée un nouveau bouton de menu dans l'éditeur Unity
    [MenuItem("Outils/Générer le Manifeste de Fichiers")]
    private static void GenerateManifest()
    {
        string streamingAssetsPath = Application.streamingAssetsPath;
        string manifestPath = Path.Combine(streamingAssetsPath, "config_manifest.txt");

        // 1. Trouver TOUS les fichiers dans StreamingAssets, de manière récursive
        string[] allFiles = Directory.GetFiles(streamingAssetsPath, "*.*", SearchOption.AllDirectories);

        StringBuilder manifestContent = new StringBuilder();
        foreach (string filePath in allFiles)
        {
            // Ignorer les fichiers "meta" de Unity et le manifeste lui-même
            if (filePath.EndsWith(".meta") || filePath.EndsWith(manifestPath))
            {
                continue;
            }

            // Convertir le chemin absolu en chemin relatif
            string relativePath = filePath.Substring(streamingAssetsPath.Length + 1);

            // Remplacer les anti-slashs Windows (\) par des slashes (/)
            relativePath = relativePath.Replace('\\', '/');

            manifestContent.AppendLine(relativePath);
        }

        // 3. Écrire le nouveau fichier manifeste
        File.WriteAllText(manifestPath, manifestContent.ToString());

        // 4. Rafraîchir l'Asset Database
        AssetDatabase.Refresh();

        Debug.Log($"SUCCÈS : 'config_manifest.txt' a été mis à jour.");
    }
}