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
using System.Reflection;
using System.Xml.Serialization;
using EmbySub.Configuration;

namespace EmbySub.Api
{
    [Route("/rest/getAlbum", "GET")]
    public class BrowsingGetAlbum : IReturn<EmbySub.Response>
    {
        [ApiMember(Name = "u", Description = "Username of Emby user", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string? Username { get; set; }

        [ApiMember(Name = "p", Description = "Password of Emby user", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string? Password { get; set; }

        [ApiMember(Name = "id", Description = "Emby ID of album", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string? AlbumId { get; set; }
    }

    public partial class SubsonicService : IService, IRequiresRequest
    {
        public async Task<object> Get(BrowsingGetAlbum request)
        {
          return null;
        }
    }
}
