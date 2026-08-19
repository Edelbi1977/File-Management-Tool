using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace MyApp.Models
{

    public partial class ArchivedFileExt : ObservableObject
    {

        [ObservableProperty]
        private string _Extenstion;

        [ObservableProperty]
        private bool _Disabled = false;

        [ObservableProperty]
        private UInt32 _Count = 0;

        [ObservableProperty]
        private UInt64 _TotalSize = 0;

        public ArchivedFileExt(string FileExt) { 
        
            if (string.IsNullOrEmpty(FileExt))
            {
                throw new ArgumentNullException("File Ext can't be null or empty");
            }

            _Extenstion = FileExt;

        }

        public override bool Equals(object? obj)
        {
            if (obj == null) return false;

            try
            {
                ArchivedFileExt X = (ArchivedFileExt)obj;
                return X.Extenstion.ToLower().Equals(Extenstion.ToLower());
            } catch
            {
                return false;

            }


        }

        public override int GetHashCode()
        {
            if (string.IsNullOrEmpty(Extenstion))
            {
                return 0;
            }
            else
            {
                return Extenstion.ToLower().GetHashCode();
            }

        }
    }


    public partial class FileCategory : ObservableObject
    {

        public FileCategory(string Name = "")
        {
            if (!string.IsNullOrEmpty(Name))
            {
                _name = Name;
            } else
            {
                _name = "Other Files Types";
            }
        }

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private List<ArchivedFileExt> _extensions = new List<ArchivedFileExt>();

        [ObservableProperty]
        private UInt32 _FileCount = 0;

        [ObservableProperty]
        private UInt64 _TotalSize = 0;


        [ObservableProperty]
        private bool _OrganizeByYear = false;

        [ObservableProperty]
        private bool _OrganizeByMonth = false;

        [ObservableProperty]
        private bool _OrganizeByType = false;

        [ObservableProperty]
        private bool _OrganizeByLocation = false;

        [ObservableProperty]
        private bool _Selected = false;


        public bool CheckIn(ArchivedFile SrcFile)
        {

            if (string.IsNullOrEmpty(Name))
            {
                FileCount ++;
                TotalSize += SrcFile.FileSize;
                return true;
            }

            foreach (ArchivedFileExt Ext in Extensions)
            {

                if (Ext.Extenstion.ToLower() == SrcFile.FileExtension.ToLower())
                {
                    Ext.Count++;
                    Ext.TotalSize += SrcFile.FileSize;

                    FileCount++;
                    TotalSize += SrcFile.FileSize;
                    return true;
                }
            }

            return false;
        }


        public bool AddExtention (string ext)
        {
            if (string.IsNullOrEmpty(ext)) { return false ; }
            ArchivedFileExt X = new ArchivedFileExt(ext);

            if (Extensions.Contains (X)) { return false; }
            Extensions.Add (X);
            return  true;
        }

        public bool RemoveExtention(string ext)
        {
            if (string.IsNullOrEmpty(ext)) { return false; }
            ArchivedFileExt X = new ArchivedFileExt(ext);
            int Idx = Extensions.IndexOf (X);
            if (Idx >= 0) {
                Extensions.RemoveAt(Idx);
                return true;
            } else
            {
                return false;
            }
           
        }


      
            
              
        public override bool Equals(object? obj)
        {
            try
            {
                if (obj == null) {   return false; }
                FileCategory X = (FileCategory)obj;
                return Name.Equals(X.Name);

            } catch
            {
                return false;
            }
                      
        }

        public override int GetHashCode()
        {
            if (string.IsNullOrEmpty(Name))
            {
               return 0;
            } else
            {
               return Name.ToLower().GetHashCode();
            }
           
        }
    }
}
