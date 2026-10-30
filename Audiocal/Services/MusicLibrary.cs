using System.IO;

namespace Audiocal.Services
{
    public class MusicLibrary
    {
        public string RootFolder { get; }

        public List<string> LibraryFolders { get; private set; }

        public MusicLibrary(string rootFolder)
        {
            RootFolder = rootFolder;
            LibraryFolders = new List<string>();

            LoadLibraryFolders();
        }

        private void LoadLibraryFolders()
        {
            LibraryFolders.Clear();

            if (!Directory.Exists(RootFolder))
                return;

            string[] folders = Directory.GetDirectories(RootFolder);

            foreach (string folder in folders)
            {
                LibraryFolders.Add(folder);
            }
        }

        public string? CreateLibrary(string libraryName)
        {
            string folderPath = Path.Combine(RootFolder, libraryName);

            if (Directory.Exists(folderPath))
            {
                return null;
            }

            Directory.CreateDirectory(folderPath);

            LibraryFolders.Add(folderPath);

            return folderPath;
        }
        public void RemoveLibrary(string folderPath)
        {
            LibraryFolders.Remove(folderPath);
        }
        public async Task<bool> RenameLibraryAsync(string oldFolderPath, string newName)
        {
            if (!Directory.Exists(oldFolderPath))
                return false;

            string? parentFolder = Path.GetDirectoryName(oldFolderPath);

            if (parentFolder == null)
                return false;

            string newFolderPath =
                Path.Combine(parentFolder, newName);

            if (Directory.Exists(newFolderPath))
                return false;

            // Retry several times if Windows still has a file open.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        Directory.Move(oldFolderPath, newFolderPath);
                    });

                    int index = LibraryFolders.IndexOf(oldFolderPath);

                    if (index >= 0)
                    {
                        LibraryFolders[index] = newFolderPath;
                    }

                    return true;
                }
                catch (IOException)
                {
                    // Windows probably still has the file locked.
                    // Wait briefly and try again.
                    await Task.Delay(200);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(200);
                }
            }

            return false;
        }
    }
}