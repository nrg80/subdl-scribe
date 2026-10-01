using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubdlScribe.Api
{
    /// <summary>
    /// (19.09.2026): JF 12.1 strips &lt;script src&gt; tags embedded in plugin
    /// configuration pages, so the page JavaScript is not executed. This controller
    /// serves the configuration JavaScript through a real API route, which JF does
    /// not strip and which the browser loads normally.
    /// </summary>
    [ApiController]
    [Route("Plugins/SubdlSync")]
    public class SubdlConfigController : ControllerBase
    {
        /// <summary>
        /// Serve the plugin configuration page JavaScript.
        /// </summary>
        /// <returns>The embedded configPage.js resource.</returns>
        [HttpGet("ConfigJs")]
        [AllowAnonymous]
        public IActionResult GetConfigJs()
        {
            var assembly = typeof(SubdlConfigController).Assembly;
            using var stream = assembly.GetManifestResourceStream("Jellyfin.Plugin.SubdlScribe.Configuration.configPage.js");
            if (stream == null)
            {
                return NotFound("configPage.js embedded resource not found");
            }

            using var reader = new StreamReader(stream);
            var js = reader.ReadToEnd();
            return Content(js, "text/javascript");
        }
    }
}
