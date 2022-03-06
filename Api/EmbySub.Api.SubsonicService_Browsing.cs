using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Users;
using MediaBrowser.Model.IO;
using MediaBrowser.Model;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Runtime;
using System.Linq;
using System.Xml.Serialization;
using EmbySub.Configuration;
using System.Text.RegularExpressions;

namespace EmbySub.Api
{
    [Route("/rest/getAlbum", "GET", Description = "Returns an individual album")]
    public class BrowsingGetAlbum : SystemBase
    {
        [ApiMember(Name = "Album ID", Description = "Emby ID of album", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string? id { get; set; }
    }

    [Route("/rest/getArtists", "GET", Description = "Returns an ID3 (non-folder) list of all artists")]
    public class BrowsingGetArtists : SystemBase
    {
      [ApiMember(Name = "Music Folder ID", Description = "Emby ID of music folder", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
      public string? musicFolderId { get; set; }
    }

    [Route("/rest/getIndexes", "GET", Description = "Returns an indexed list of all artists")]
    public class BrowsingGetIndexes : SystemBase
    {
      [ApiMember(Name = "Music Folder ID", Description = "Emby ID of music folder", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
      public string? musicFolderId { get; set; }
    }

    [Route("/rest/getMusicFolders", "GET", Description = "Returns top level music folder configured for plugin")]
    public class BrowsingGetMusicFolders : SystemBase
    {
    }

    [Route("/rest/getMusicDirectory", "GET", Description = "Returns specific music directory")]
    public class BrowsingGetMusicDirectory : SystemBase
    {
      [ApiMember(Name = "Music Folder ID", Description = "Emby ID of music folder", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
      public string? id { get; set; }
    }

    public class ArtistPackage
    {
      public HashSet<String> albums { get; set; }
      public String artistName { get; set; }

      public String artistId { get; set; }
    }

    public partial class SubsonicService : IService, IRequiresRequest
    {
        public async Task<object> Get(BrowsingGetMusicDirectory req)
        {
          BrowsingGetIndexes bgi = new BrowsingGetIndexes();
          bgi.musicFolderId = req.id;
          bgi.u = req.u;
          bgi.p = req.p;
          return await this.Get(bgi);
        }

        public async Task<object> Get(BrowsingGetArtists req)
        {
          HttpResponseMessage hrm = await Login(req);
          var subReq = new EmbySub.Response();
          String hrmraw, xmlString, s;

          // if login is NOT successful return an error....
          if (!hrm.IsSuccessStatusCode)
          {
            subReq.ItemElementName = EmbySub.ItemChoiceType.error;
            EmbySub.Error e = new EmbySub.Error();
            e.code = 0;
            e.message = "Login failed";
            subReq.Item = e;
            subReq.version = SupportedSubsonicApiVersion;
            xmlString = Serializer<EmbySub.Response>.Serialize(subReq);
            return ResultFactory.GetResult(Request, xmlString, null);
          }
          // otherwise it was successful so grab & store the auth token
          else
          {
            hrmraw = await hrm.Content.ReadAsStringAsync();
            JsonDocument doc = JsonDocument.Parse(hrmraw);
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            c.DefaultRequestHeaders.Add("X-Emby-Token", doc.RootElement.GetProperty("AccessToken").ToString());
          }


          // let's get a list of all ID3 songs
          String url = String.Format("http://emby.localdomain:{0}/emby/Items?IncludeItemTypes=Audio&Recursive=true", Plugin.Instance.Configuration.LocalEmbyPort);
          HttpResponseMessage mes = await c.GetAsync(url);
          hrmraw = await mes.Content.ReadAsStringAsync();
          JsonDocument k = JsonDocument.Parse(hrmraw);
          JsonElement allID3Songs = k.RootElement.GetProperty("Items");

          // now store all albums UNIQUELY per artist
          Dictionary<String, HashSet<String>> d = new Dictionary<String, HashSet<String>>();

          // make an artist ID lookup dictionary for convenience
          Dictionary<String, String> artistWithId = new Dictionary<String, String>();

          String albumArtist = string.Empty;
          String artistId = string.Empty;
          JsonElement je;
          foreach (JsonElement song in allID3Songs.EnumerateArray())
          {
            if (!song.TryGetProperty("AlbumArtist", out je) || !song.TryGetProperty("Album", out je))
            {
              continue;
            }

            foreach (JsonElement items in song.GetProperty("ArtistItems").EnumerateArray())
            {
              artistId = items.GetProperty("Id").ToString();
              albumArtist = items.GetProperty("Name").ToString();
              break;
            }

            // not worrying about artists that don't begin with a letter right now
            if(!Char.IsLetter(albumArtist[0]))
            {
              continue;
            }

            if (!d.ContainsKey(albumArtist))
            {
              d.Add(albumArtist, new HashSet<String>());
            }

            String album = song.GetProperty("Album").ToString();

            d[albumArtist].Add(album);

            if (!artistWithId.ContainsKey(albumArtist))
            {
              artistWithId.Add(albumArtist, artistId);
            }
          }

          var sortedDict = from entry in d orderby entry.Key ascending select entry;

          // now let's put each artist/albumlist pair into a queue for easier processing with a custom object
          Queue<ArtistPackage> q = new Queue<ArtistPackage>();
          foreach(KeyValuePair<string, HashSet<String>> entry in sortedDict)
          {
            ArtistPackage ap = new ArtistPackage();
            ap.artistName = entry.Key;
            ap.albums = entry.Value;
            ap.artistId = artistWithId[ap.artistName];
            q.Enqueue(ap);
          }

          char[] alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();

          EmbySub.ArtistsID3 artistListID3 = new EmbySub.ArtistsID3();
          List<EmbySub.IndexID3> letterIndex = new List<EmbySub.IndexID3>();
          
          // for each letter of the alphabet....
          foreach(char c in alpha)
          {
            ArtistPackage artpac;
            if (q.TryPeek(out artpac))
            {
              List<EmbySub.ArtistID3> artistIndex = new List<EmbySub.ArtistID3>();

              char ch = q.Peek().artistName[0];

              // ....let's get all artists that begin with that letter
              while (Char.ToUpper(ch).Equals(Char.ToUpper(c)))
              {
                ArtistPackage arp = q.Dequeue();
                EmbySub.ArtistID3 toAdd = new EmbySub.ArtistID3();
                toAdd.name = arp.artistName;
                toAdd.albumCount = arp.albums.Count;
                toAdd.id = arp.artistId;
                artistIndex.Add(toAdd);
                if (!q.TryPeek(out artpac))
                {
                  break;
                }
                else
                {
                  ch = q.Peek().artistName[0];
                }
              }

              // if any artists for that letter existed, add the index to the master indexes list
              if (artistIndex.Any())
              {
                EmbySub.IndexID3 idx = new EmbySub.IndexID3();
                idx.name = Char.ToUpper(c).ToString();
                idx.artist = artistIndex.ToArray();
                letterIndex.Add(idx);
              }
            }
            else
            {
              break;
            }
          }


          artistListID3.index = letterIndex.ToArray();

          subReq.Item = artistListID3;
          subReq.ItemElementName = EmbySub.ItemChoiceType.artists;
          subReq.version = SupportedSubsonicApiVersion;
          xmlString = Serializer<EmbySub.Response>.Serialize(subReq);

          url = String.Format("http://localhost:{0}/emby/Sessions/Logout", Plugin.Instance.Configuration.LocalEmbyPort);
          await c.PostAsync(url, null);
          return ResultFactory.GetResult(Request, xmlString, null);
        }

        public async Task<object> Get(BrowsingGetMusicFolders req)
        {
          HttpResponseMessage hrm = await Login(req);
          var subReq = new EmbySub.Response();
          String hrmraw, xmlString, s;

          // if login is NOT successful return an error....
          if (!hrm.IsSuccessStatusCode)
          {
            subReq.ItemElementName = EmbySub.ItemChoiceType.error;
            EmbySub.Error e = new EmbySub.Error();
            e.code = 0;
            e.message = "Login failed";
            subReq.Item = e;
            subReq.version = SupportedSubsonicApiVersion;
            xmlString = Serializer<EmbySub.Response>.Serialize(subReq);
            return ResultFactory.GetResult(Request, xmlString, null);
          }
          // otherwise it was successful so grab & store the auth token
          else
          {
            hrmraw = await hrm.Content.ReadAsStringAsync();
            JsonDocument doc = JsonDocument.Parse(hrmraw);
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            c.DefaultRequestHeaders.Add("X-Emby-Token", doc.RootElement.GetProperty("AccessToken").ToString());
          }

          // we need the unique ID for the desired music library set in the plugin configuration
          String url = String.Format("http://localhost:{0}/emby/Items?IncludeItemTypes=Album&ExcludeItemTypes=Audio", Plugin.Instance.Configuration.LocalEmbyPort);
          HttpResponseMessage mes = await c.GetAsync(url);
          hrmraw = await mes.Content.ReadAsStringAsync();
          JsonDocument j = JsonDocument.Parse(hrmraw);
          JsonElement allLibs = j.RootElement.GetProperty("Items");
          String musicLibId = String.Empty;

          List<EmbySub.MusicFolder> mfl = new List<EmbySub.MusicFolder>();
          EmbySub.MusicFolders m = new EmbySub.MusicFolders();

          foreach (JsonElement lib in allLibs.EnumerateArray())
          {
            s = lib.GetProperty("Name").ToString();
            if(String.Equals(s, Plugin.Instance.Configuration.MusicLibraryName))
            {
              EmbySub.MusicFolder mf = new EmbySub.MusicFolder();
              mf.name = s;
              mf.id = Int32.Parse(lib.GetProperty("Id").ToString());
              musicLibId = mf.id.ToString();
              mfl.Add(mf);
              break;
            }
          }

          // music library does not exist under configured name
          if (String.IsNullOrEmpty(musicLibId))
          {
            subReq.ItemElementName = EmbySub.ItemChoiceType.error;
            EmbySub.Error e = new EmbySub.Error();
            e.code = 0;
            e.message = "Music library does not exist";
            subReq.Item = e;
            subReq.version = SupportedSubsonicApiVersion;
            xmlString = Serializer<EmbySub.Response>.Serialize(subReq);
            return ResultFactory.GetResult(Request, xmlString, null);
          }

          m.musicFolder = mfl.ToArray();

          subReq.Item = m;
          subReq.ItemElementName = EmbySub.ItemChoiceType.musicFolders;
          subReq.version = SupportedSubsonicApiVersion;
          xmlString = Serializer<EmbySub.Response>.Serialize(subReq);

          url = String.Format("http://localhost:{0}/emby/Sessions/Logout", Plugin.Instance.Configuration.LocalEmbyPort);
          await c.PostAsync(url, null);
          return ResultFactory.GetResult(Request, xmlString, null);
        }
        public async Task<object> Get(BrowsingGetIndexes req)
        {
          HttpResponseMessage hrm = await Login(req);
          var subReq = new EmbySub.Response();
          String hrmraw, xmlString, s;

          // if login is NOT successful return an error....
          if (!hrm.IsSuccessStatusCode)
          {
            subReq.ItemElementName = EmbySub.ItemChoiceType.error;
            EmbySub.Error e = new EmbySub.Error();
            e.code = 0;
            e.message = "Login failed";
            subReq.Item = e;
            subReq.version = SupportedSubsonicApiVersion;
            xmlString = Serializer<EmbySub.Response>.Serialize(subReq);
            return ResultFactory.GetResult(Request, xmlString, null);
          }
          // otherwise it was successful so grab & store the auth token
          else
          {
            hrmraw = await hrm.Content.ReadAsStringAsync();
            JsonDocument doc = JsonDocument.Parse(hrmraw);
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            c.DefaultRequestHeaders.Add("X-Emby-Token", doc.RootElement.GetProperty("AccessToken").ToString());
          }

          // we need the unique ID for the desired music library set in the plugin configuration
          String url = String.Format("http://localhost:{0}/emby/Items?IncludeItemTypes=Album&ExcludeItemTypes=Audio", Plugin.Instance.Configuration.LocalEmbyPort);
          HttpResponseMessage mes = await c.GetAsync(url);
          hrmraw = await mes.Content.ReadAsStringAsync();
          JsonDocument j = JsonDocument.Parse(hrmraw);
          JsonElement allLibs = j.RootElement.GetProperty("Items");
          String musicLibId = String.Empty;

          foreach (JsonElement lib in allLibs.EnumerateArray())
          {
            s = lib.GetProperty("Name").ToString();
            if(String.Equals(s, Plugin.Instance.Configuration.MusicLibraryName))
            {
              musicLibId = lib.GetProperty("Id").ToString();
              break;
            }
          }

          // music library does not exist under configured name
          if (String.IsNullOrEmpty(musicLibId))
          {
            subReq.ItemElementName = EmbySub.ItemChoiceType.error;
            EmbySub.Error e = new EmbySub.Error();
            e.code = 0;
            e.message = "Music library does not exist";
            subReq.Item = e;
            subReq.version = SupportedSubsonicApiVersion;
            xmlString = Serializer<EmbySub.Response>.Serialize(subReq);
            return ResultFactory.GetResult(Request, xmlString, null);
          }

          // we want a certain music folder, so let's get all of them first
          if (!String.IsNullOrEmpty(req.musicFolderId))
          {
            url = String.Format("http://localhost:{0}/emby/Items?ParentId={1}&Recursive=true&IncludeItemTypes=Folder&Fields=ParentId", Plugin.Instance.Configuration.LocalEmbyPort, musicLibId);
            mes = await c.GetAsync(url);
            hrmraw = await mes.Content.ReadAsStringAsync();
            j = JsonDocument.Parse(hrmraw);
            JsonElement allFolders = j.RootElement.GetProperty("Items");
            EmbySub.Directory d = new EmbySub.Directory();
            List<EmbySub.Child> l  = new List<EmbySub.Child>();

            // for each folder let's find out if it is a parent or child music directory and add to the appropriate collection
            foreach (JsonElement folder in allFolders.EnumerateArray())
            {
              if (folder.GetProperty("Id").ToString().Equals(req.musicFolderId))
              {
                d.parent = folder.GetProperty("ParentId").ToString();
                d.id = folder.GetProperty("Id").ToString();
                d.name = folder.GetProperty("Name").ToString();
              }
              else if (folder.GetProperty("ParentId").ToString().Equals(req.musicFolderId))
              {
                EmbySub.Child c = new EmbySub.Child();
                c.id = folder.GetProperty("Id").ToString();
                c.parent = folder.GetProperty("ParentId").ToString();
                c.title = folder.GetProperty("Name").ToString();
                c.artist = d.name;
                c.isDir = folder.GetProperty("IsFolder").GetBoolean();
                c.coverArt = folder.GetProperty("ImageTags").GetProperty("Primary").ToString();
                l.Add(c);
              }
            }

            d.child = l.ToArray();

            subReq.Item = d;
            subReq.ItemElementName = EmbySub.ItemChoiceType.directory;
            subReq.version = SupportedSubsonicApiVersion;
            xmlString = Serializer<EmbySub.Response>.Serialize(subReq);

            url = String.Format("http://localhost:{0}/emby/Sessions/Logout", Plugin.Instance.Configuration.LocalEmbyPort);
            await c.PostAsync(url, null);
            return ResultFactory.GetResult(Request, xmlString, null);
          }

          // library exists so first let's get all artists....
          url = String.Format("http://localhost:{0}/emby/Items?ParentId={1}&SortBy=Name", Plugin.Instance.Configuration.LocalEmbyPort, musicLibId);
          mes = await c.GetAsync(url);
          hrmraw = await mes.Content.ReadAsStringAsync();
          j = JsonDocument.Parse(hrmraw);
          JsonElement allArtists = j.RootElement.GetProperty("Items");
          Queue<JsonElement> q = new Queue<JsonElement>();

          // ....and put them in a FIFO Queue
          foreach (JsonElement artist in allArtists.EnumerateArray())
          {
            q.Enqueue(artist);
          }

          EmbySub.Indexes indexes = new EmbySub.Indexes();
          List<EmbySub.Index> letterIndex = new List<EmbySub.Index>();

          char[] alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToCharArray();

          // for each letter of the alphabet....
          foreach(char c in alpha)
          {
            JsonElement r;
            if (q.TryPeek(out r))
            {
              List<EmbySub.Artist> artistList = new List<EmbySub.Artist>();

              // if there are any artists that begin with this letter, dequeue and add to index's artist array
              char ch = q.Peek().GetProperty("Name").ToString()[0];
              while (ch.Equals(c))
              {
                JsonElement je = q.Dequeue();
                EmbySub.Artist a = new EmbySub.Artist();
                a.id = je.GetProperty("Id").ToString();
                a.name = je.GetProperty("Name").ToString();
                artistList.Add(a);
                if (!q.TryPeek(out r))
                {
                  break;
                }
                else
                {
                  ch = q.Peek().GetProperty("Name").ToString()[0];
                }
              }

              // if any artists for that letter existed, add the index to the master indexes list
              if (artistList.Any())
              {
                EmbySub.Index i = new EmbySub.Index();
                i.name = c.ToString();
                i.artist = artistList.ToArray();
                letterIndex.Add(i);
              }
            }
            else
            {
              break;
            }
          }

          indexes.index = letterIndex.ToArray();

          subReq.Item = indexes;
          subReq.ItemElementName = EmbySub.ItemChoiceType.indexes;
          subReq.version = SupportedSubsonicApiVersion;
          xmlString = Serializer<EmbySub.Response>.Serialize(subReq);

          url = String.Format("http://localhost:{0}/emby/Sessions/Logout", Plugin.Instance.Configuration.LocalEmbyPort);
          await c.PostAsync(url, null);
          return ResultFactory.GetResult(Request, xmlString, null);
        }
        public async Task<object> Get(BrowsingGetAlbum req)
        {
         HttpResponseMessage hrm = await Login(req);
          var subReq = new EmbySub.Response();
          String hrmraw, xmlString;

          // if login is NOT successful return an error....
          if (!hrm.IsSuccessStatusCode)
          {
            subReq.ItemElementName = EmbySub.ItemChoiceType.error;
            EmbySub.Error e = new EmbySub.Error();
            e.code = 0;
            e.message = "Login failed";
            subReq.Item = e;
            subReq.version = SupportedSubsonicApiVersion;
            xmlString = Serializer<EmbySub.Response>.Serialize(subReq);
            return ResultFactory.GetResult(Request, xmlString, null);
          }
          // otherwise it was successful so grab & store the auth token
          else
          {
            hrmraw = await hrm.Content.ReadAsStringAsync();
            JsonDocument doc = JsonDocument.Parse(hrmraw);
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            c.DefaultRequestHeaders.Add("X-Emby-Token", doc.RootElement.GetProperty("AccessToken").ToString());
          }

          // first we get album (not the songs)
          String url = String.Format("http://localhost:{0}/emby/Items?Ids={1}&Fields=ParentId", Plugin.Instance.Configuration.LocalEmbyPort, req.id);
          HttpResponseMessage mes = await c.GetAsync(url);
          hrmraw = await mes.Content.ReadAsStringAsync();
          JsonDocument j = JsonDocument.Parse(hrmraw);
          JsonElement album = j.RootElement.GetProperty("Items")[0];
          JsonElement artist = album.GetProperty("ArtistItems")[0];

          // then we get the album's songs
          url = String.Format("http://localhost:{0}/emby/Items?ParentId={1}", Plugin.Instance.Configuration.LocalEmbyPort, req.id);
          mes = await c.GetAsync(url);
          hrmraw = await mes.Content.ReadAsStringAsync();
          JsonDocument k = JsonDocument.Parse(hrmraw);
          JsonElement ss = k.RootElement.GetProperty("Items");

          List<EmbySub.Child> songs = new List<EmbySub.Child>();

          // ....and then add each song to our list of songs for the album
          foreach (JsonElement song in ss.EnumerateArray())
          {
            EmbySub.Child ch = new EmbySub.Child();
            ch.id = song.GetProperty("Id").ToString();
            ch.title = song.GetProperty("Name").ToString();
            ch.album = album.GetProperty("Name").ToString();
            ch.artist = artist.GetProperty("Name").ToString();
            ch.isDir = song.GetProperty("IsFolder").GetBoolean();
            ch.albumId = req.id;
            ch.artistId = artist.GetProperty("Id").ToString();
            ch.durationSpecified = true;
            ch.duration = (int)TimeSpan.FromTicks(song.GetProperty("RunTimeTicks").GetInt64()).TotalSeconds;
            songs.Add(ch);
          }

          Child[] songArray = songs.ToArray();

          // then we create the main album record....
          subReq.ItemElementName = EmbySub.ItemChoiceType.album;
          EmbySub.AlbumWithSongsID3 a = new EmbySub.AlbumWithSongsID3();
          a.id = req.id;
          a.name = album.GetProperty("Name").ToString();
          a.artist = artist.GetProperty("Name").ToString();
          a.artistId = artist.GetProperty("Id").ToString();
          a.songCount = Int32.Parse(k.RootElement.GetProperty("TotalRecordCount").ToString());
          a.duration = (int)TimeSpan.FromTicks(album.GetProperty("RunTimeTicks").GetInt64()).TotalSeconds;

          // ....and the album's song list to that record
          a.song = songArray;

          subReq.Item = a;
          subReq.version = SupportedSubsonicApiVersion;
          xmlString = Serializer<EmbySub.Response>.Serialize(subReq);

          url = String.Format("http://localhost:{0}/emby/Sessions/Logout", Plugin.Instance.Configuration.LocalEmbyPort);
          await c.PostAsync(url, null);
          return ResultFactory.GetResult(Request, xmlString, null);
        }
    }
}
