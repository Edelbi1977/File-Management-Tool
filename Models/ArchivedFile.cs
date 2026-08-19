using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MyApp.Models;

    public class ArchivedFile
    {

    private static UInt32 _nextID = 1;

    UInt32 _id = 0;
    string _filename = "";
    string _fileextension = "";
    string _DirPath = "";

    string _SourcePath;
    long _filedate = 0;
    long _filesize = 0;
    long _contentsdate = 0;
    byte[] _ContentHash = [];
    
     

    public ArchivedFile(string SourceFilePath)
        {
            _id = NewID;
            _SourcePath = SourceFilePath;

            if (File.Exists(SourceFilePath))
            {
                _filedate = File.GetLastWriteTimeUtc(SourceFilePath).ToBinary();
                _filesize = new FileInfo(SourceFilePath).Length;
                _filename = Path.GetFileNameWithoutExtension(SourceFilePath);
                _fileextension = Path.GetExtension(SourceFilePath);
                
            using (var stream = File.OpenRead(SourceFilePath))
                {
                    using (var md5 = System.Security.Cryptography.MD5.Create())
                    {

                        MediaContentHasher MCH = new MediaContentHasher(SourceFilePath);
                        try
                        {
                           
                            _ContentHash = MCH.ComputeContentHash();

                        }
                        catch
                        {
                            _ContentHash = MCH.ComputeSha1(0,_filesize);
                        }
                                                
                    }
                }
            }
            

        }

    public ulong FileSize
    {
        get
        {
            return (ulong)_filesize;
        }
    } 


    public string FileExtension
    {
        get
        {
            return _fileextension;
        }
    } 



    public string Filepath
    {
        get
        {
            if (_SourcePath != null)
            {
                return _SourcePath;
            }
            
            else
            {
                return string.Empty;
            }
        }
    }

        public static UInt32 NewID
        {
            get
            {
                UInt32 Res = _nextID;
                _nextID++;
                return Res;
            }
        }

        

        public override int GetHashCode()
        {
            if (_ContentHash == null || _ContentHash.Length == 0)
                return _id.GetHashCode();

            // Combine hash bytes into a single int
            int hash = 17;
            foreach (byte b in _ContentHash)
            {
                hash = hash * 31 + b;
            }
            return hash;
        }

        public override bool Equals(object? obj)
        {
            try
            {
                if (obj == null) return false;
                ArchivedFile X = (ArchivedFile)obj;
                if (X._id == _id)
                {
                    return true;
                } else
                {
                    for (int c = 0; c < _ContentHash.Length; c++)
                    {
                        if (X._ContentHash[c] != _ContentHash[c]) return false;
                    }
                    return true;
                }


            
            } catch
            {
                return false;
            }
         
        }

    }

