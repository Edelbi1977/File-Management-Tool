using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace MyApp.Models
{

    public partial class ArchiveStore :ObservableObject
    {

        private ArchiveStore() {
            ChangeRoot();
            _CatList.Add(new FileCategory());
        }
        private static readonly Lazy<ArchiveStore> _instance = new (() => new ArchiveStore());
        public static ArchiveStore CurrentRoot => _instance.Value;

        private string _RootPath = AppContext.BaseDirectory;

        private ObservableCollection<FileCategory> _CatList = new ObservableCollection<FileCategory>();

        public bool ChangeRoot(string RootDirPath = "")
        {
            try
            {
                if (EnsureDirectoryExists (RootDirPath))
            {
                _RootPath = RootDirPath;
              
            }
            else
            {
                _RootPath = AppContext.BaseDirectory;
            }
                return true;
            } catch
            {
                return false;
            }
                    
        }

        public string RootPath { get { return _RootPath; }        
        }

        public static bool EnsureDirectoryExists(string DirPath)
        {
            if (string.IsNullOrWhiteSpace(DirPath))
            {
                return false;
            }

            try
            {
                if (Directory.Exists(DirPath))
                {
                    return true;
                }

                Directory.CreateDirectory(DirPath);
                return true;
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or
                     IOException or
                     PathTooLongException or
                     ArgumentException or
                     NotSupportedException)
            {
                return false;
            }
        }

        public ObservableCollection<FileCategory> Categories
        {
            get
            {
                return _CatList;
            }
        }

        public readonly List<ArchivedFile> Files = new List<ArchivedFile>();

        private readonly FileCategory _Default = new FileCategory();


      

    }
}
