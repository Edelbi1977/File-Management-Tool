using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MyApp.Models;

    public abstract class ArchivedFile
    {

    private static uint _nextId = 1;

    private readonly uint _id = 0;
    private string _filename = "";
    private readonly string _fileextension = "";
    private string _dirPath = "";

    private string _SourcePath;
    private long _filedate = 0;
    private long _filesize = 0;
    private long _contentsdate = 0;
    readonly byte[] _ContentHash = [];
    
     

    public ArchivedFile(string sourceFilePath)
        {
            _id = NewId;
            _SourcePath = sourceFilePath;

            if (!File.Exists(sourceFilePath)) return;
            
            _filedate = File.GetLastWriteTimeUtc(sourceFilePath).ToBinary();
            _filesize = new FileInfo(sourceFilePath).Length;
            _filename = Path.GetFileNameWithoutExtension(sourceFilePath);
            _fileextension = Path.GetExtension(sourceFilePath);

            using var stream = File.OpenRead(sourceFilePath);
            using var md5 = System.Security.Cryptography.MD5.Create();
            var mch = new MediaContentHasher(sourceFilePath);
            try
            {
                           
                _ContentHash = mch.ComputeContentHash();

            }
            catch
            {
                _ContentHash = mch.ComputeSha1(0,_filesize);
            }


        }

    public ulong FileSize => (ulong)_filesize;


    public string FileExtension => _fileextension;


    public string Filepath
    {
        get
        {
            return _SourcePath;
        }
    }

        private static uint NewId
        {
            get
            {
                var res = _nextId;
                _nextId++;
                return res;
            }
        }

        

        public override int GetHashCode()
        {
            if (_ContentHash.Length == 0)
                return _id.GetHashCode();

            // Combine hash bytes into a single int
            return _ContentHash.Aggregate(17, (current, b) => current * 31 + b);
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

