using Audiocal.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Audiocal.Services
{
    public class MetadataService
    {
        public Song GetSongMetadata(string filePath)
        {
            using var file = TagLib.File.Create(filePath);

            Song song = new Song();

            song.FilePath = filePath;
            song.Title = file.Tag.Title ?? "";
            song.Artist = file.Tag.FirstPerformer ?? "";
            

            return song;
        }

        public void UpdateSongTitle(string filePath, string title)
        {
            using var file = TagLib.File.Create(filePath);
            file.Tag.Title = title;
            file.Save();
        }

        public void UpdateSongArtist(string filePath, string artist)
        {
            using var file = TagLib.File.Create(filePath);
            file.Tag.Performers = new[] { artist };
            file.Save();
        }
    }
}
