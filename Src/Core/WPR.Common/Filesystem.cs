using System;
using System.Collections.Generic;
using System.IO;

#if __ANDROID__
using Android.Content.Res;
#endif

namespace WPR.Common
{
    public static class Filesystem
    {
        // https://stackoverflow.com/questions/58744/copy-the-entire-contents-of-a-directory-in-c-sharp
        public static void CopyFilesRecursively(string sourcePath, string targetPath)
        {
            Directory.CreateDirectory(targetPath);

            //Now Create all of the directories
            foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourcePath, targetPath));
            }

            //Copy all the files & Replaces any files with the same name
            foreach (string newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
            {
                File.Copy(newPath, newPath.Replace(sourcePath, targetPath), true);
            }
        }

#if __ANDROID__
        public static void CopyFolderFromAssets(AssetManager assets, string sourcePath, string targetPath)
        {
            string[]? assetNames = assets.List(sourcePath);
            if ((assetNames == null) || (assetNames.Length == 0))
            {
                return;
            }

            Directory.CreateDirectory(targetPath);

            foreach (string name in assetNames)
            {
                string childSource = sourcePath + "/" + name;
                string childTarget = Path.Combine(targetPath, name);

                // AssetManager.List returns children for a directory and an empty
                // array for a file — recurse into subfolders (e.g. the per-product
                // achievement catalogue folders) rather than trying to Open them.
                string[]? children = assets.List(childSource);
                if (children != null && children.Length > 0)
                {
                    CopyFolderFromAssets(assets, childSource, childTarget);
                }
                else
                {
                    CopyFileFromAssets(assets, childSource, childTarget);
                }
            }
        }

        public static void CopyFileFromAssets(AssetManager assets, string sourceFile, string destFile)
        {
            using (Stream assetStream = assets.Open(sourceFile))
            {
                // FileMode.Create, not OpenOrCreate: OpenOrCreate does not truncate, so a
                // shorter replacement leaves the tail of the old file behind. This copy
                // re-runs on every launch to deploy updated achievement catalogues, and a
                // catalogue that loses a few bytes between releases would arrive as invalid
                // JSON -- which HardcodedAchievementCatalogue.Load swallows, so the game
                // simply shows no achievements rather than reporting anything.
                using (FileStream destStream = File.Open(destFile, FileMode.Create, FileAccess.Write))
                {
                    assetStream.CopyTo(destStream);
                }
            }
        }
#endif
    }
}
