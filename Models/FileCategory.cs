using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace MyApp.Models
{
    public partial class FileCategory : ObservableObject
    {

        public FileCategory(string Name = "")
        {
            if (!string.IsNullOrEmpty(Name))
            {
                _name = Name;

            }

            _name = "Other Files Types";

            _OrganizingFolders.Add("Year");
            _OrganizingFolders.Add("Month");
            _OrganizingFolders.Add("File Type");
            _OrganizingFolders.Add("Location");


        }

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private List<string> _extensions = new List<string>();

        [ObservableProperty]
        private UInt32 _FileCount = 0;

        [ObservableProperty]
        private UInt64 _TotalSize = 0;

        private readonly ObservableCollection<string> _OrganizingFolders = new ObservableCollection<string>();

        private readonly ObservableCollection<string> _SubFolders = new ObservableCollection<string>();

        public bool CheckIn(ArchivedFile SrcFile)
        {

            if (string.IsNullOrEmpty(Name))
            {
                FileCount ++;
                TotalSize += SrcFile.FileSize;
                return true;
            }

            foreach (var e in Extensions)
            {
                if (e.ToLower() == SrcFile.FileExtension.ToLower())
                {
                    FileCount++;
                    TotalSize += SrcFile.FileSize;
                    return true;
                }
            }

            return false;
        }


        public bool AddExtention (string ext)
        {
            if (ext == null) { return false ; }
            if (Extensions.Contains (ext.ToLower())) { return false; }
            Extensions.Add (ext.ToLower());
            return  true;
        }

        public bool RemoveExtention(string ext)
        {
            if (ext == null) { return false; }
            if (Extensions.Contains(ext.ToLower())) {
                return Extensions.Remove(ext.ToLower());
            }
            return false;
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
