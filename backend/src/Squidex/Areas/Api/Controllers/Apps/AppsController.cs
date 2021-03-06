﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschränkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Squidex.Areas.Api.Controllers.Apps.Models;
using Squidex.Domain.Apps.Entities;
using Squidex.Domain.Apps.Entities.Apps;
using Squidex.Domain.Apps.Entities.Apps.Commands;
using Squidex.Domain.Apps.Entities.Apps.Plans;
using Squidex.Infrastructure.Assets;
using Squidex.Infrastructure.Commands;
using Squidex.Infrastructure.Log;
using Squidex.Infrastructure.Security;
using Squidex.Infrastructure.Validation;
using Squidex.Shared;
using Squidex.Web;

namespace Squidex.Areas.Api.Controllers.Apps
{
    /// <summary>
    /// Manages and configures apps.
    /// </summary>
    [ApiExplorerSettings(GroupName = nameof(Apps))]
    public sealed class AppsController : ApiController
    {
        private static readonly ResizeOptions ResizeOptions = new ResizeOptions { Width = 50, Height = 50, Mode = ResizeMode.Crop };
        private readonly IAppImageStore appImageStore;
        private readonly IAssetStore assetStore;
        private readonly IAssetThumbnailGenerator assetThumbnailGenerator;
        private readonly IAppProvider appProvider;
        private readonly IAppPlansProvider appPlansProvider;

        public AppsController(ICommandBus commandBus,
            IAppImageStore appImageStore,
            IAssetStore assetStore,
            IAssetThumbnailGenerator assetThumbnailGenerator,
            IAppProvider appProvider,
            IAppPlansProvider appPlansProvider)
            : base(commandBus)
        {
            this.appImageStore = appImageStore;
            this.assetStore = assetStore;
            this.assetThumbnailGenerator = assetThumbnailGenerator;
            this.appProvider = appProvider;
            this.appPlansProvider = appPlansProvider;
        }

        /// <summary>
        /// Get your apps.
        /// </summary>
        /// <returns>
        /// 200 => Apps returned.
        /// </returns>
        /// <remarks>
        /// You can only retrieve the list of apps when you are authenticated as a user (OpenID implicit flow).
        /// You will retrieve all apps, where you are assigned as a contributor.
        /// </remarks>
        [HttpGet]
        [Route("apps/")]
        [ProducesResponseType(typeof(AppDto[]), 200)]
        [ApiPermission]
        [ApiCosts(0)]
        public async Task<IActionResult> GetApps()
        {
            var userOrClientId = HttpContext.User.UserOrClientId()!;
            var userPermissions = HttpContext.Permissions();

            var apps = await appProvider.GetUserAppsAsync(userOrClientId, userPermissions);

            var response = Deferred.Response(() =>
            {
                var userPermissions = HttpContext.Permissions();

                return apps.OrderBy(x => x.Name).Select(a => AppDto.FromApp(a, userOrClientId, userPermissions, appPlansProvider, this)).ToArray();
            });

            Response.Headers[HeaderNames.ETag] = apps.ToEtag();

            return Ok(response);
        }

        /// <summary>
        /// Get an app by name.
        /// </summary>
        /// <param name="app">The name of the app.</param>
        /// <returns>
        /// 200 => Apps returned.
        /// 404 => App not found.
        /// </returns>
        [HttpGet]
        [Route("apps/{app}")]
        [ProducesResponseType(typeof(AppDto), 200)]
        [ApiPermission]
        [ApiCosts(0)]
        public IActionResult GetApp(string app)
        {
            var response = Deferred.Response(() =>
            {
                var userOrClientId = HttpContext.User.UserOrClientId()!;
                var userPermissions = HttpContext.Permissions();

                return AppDto.FromApp(App, userOrClientId, userPermissions, appPlansProvider, this);
            });

            Response.Headers[HeaderNames.ETag] = App.ToEtag();

            return Ok(response);
        }

        /// <summary>
        /// Create a new app.
        /// </summary>
        /// <param name="request">The app object that needs to be added to Squidex.</param>
        /// <returns>
        /// 201 => App created.
        /// 400 => App request not valid.
        /// 409 => App name is already in use.
        /// </returns>
        /// <remarks>
        /// You can only create an app when you are authenticated as a user (OpenID implicit flow).
        /// You will be assigned as owner of the new app automatically.
        /// </remarks>
        [HttpPost]
        [Route("apps/")]
        [ProducesResponseType(typeof(AppDto), 201)]
        [ApiPermission]
        [ApiCosts(0)]
        public async Task<IActionResult> PostApp([FromBody] CreateAppDto request)
        {
            var response = await InvokeCommandAsync(request.ToCommand());

            return CreatedAtAction(nameof(GetApps), response);
        }

        /// <summary>
        /// Update the app.
        /// </summary>
        /// <param name="app">The name of the app to update.</param>
        /// <param name="request">The values to update.</param>
        /// <returns>
        /// 200 => App updated.
        /// 404 => App not found.
        /// </returns>
        [HttpPut]
        [Route("apps/{app}/")]
        [ProducesResponseType(typeof(AppDto), 200)]
        [ApiPermission(Permissions.AppUpdateGeneral)]
        [ApiCosts(0)]
        public async Task<IActionResult> UpdateApp(string app, [FromBody] UpdateAppDto request)
        {
            var response = await InvokeCommandAsync(request.ToCommand());

            return Ok(response);
        }

        /// <summary>
        /// Get the app image.
        /// </summary>
        /// <param name="app">The name of the app to update.</param>
        /// <param name="file">The file to upload.</param>
        /// <returns>
        /// 200 => App image uploaded.
        /// 404 => App not found.
        /// </returns>
        [HttpPost]
        [Route("apps/{app}/image")]
        [ProducesResponseType(typeof(AppDto), 200)]
        [ApiPermission(Permissions.AppUpdateImage)]
        [ApiCosts(0)]
        public async Task<IActionResult> UploadImage(string app, IFormFile file)
        {
            var response = await InvokeCommandAsync(CreateCommand(file));

            return Ok(response);
        }

        /// <summary>
        /// Get the app image.
        /// </summary>
        /// <param name="app">The name of the app.</param>
        /// <returns>
        /// 200 => App image found and content or (resized) image returned.
        /// 404 => App not found.
        /// </returns>
        [HttpGet]
        [Route("apps/{app}/image")]
        [ProducesResponseType(typeof(FileResult), 200)]
        [AllowAnonymous]
        [ApiCosts(0)]
        public IActionResult GetImage(string app)
        {
            if (App.Image == null)
            {
                return NotFound();
            }

            var etag = App.Image.Etag;

            Response.Headers[HeaderNames.ETag] = etag;

            var callback = new Func<Stream, CancellationToken, Task>(async (body, ct) =>
            {
                var resizedAsset = $"{App.Id}_{etag}_Resized";

                try
                {
                    await assetStore.DownloadAsync(resizedAsset, body);
                }
                catch (AssetNotFoundException)
                {
                    using (Profiler.Trace("Resize"))
                    {
                        using (var sourceStream = GetTempStream())
                        {
                            using (var destinationStream = GetTempStream())
                            {
                                using (Profiler.Trace("ResizeDownload"))
                                {
                                    await appImageStore.DownloadAsync(App.Id, sourceStream);
                                    sourceStream.Position = 0;
                                }

                                using (Profiler.Trace("ResizeImage"))
                                {
                                    await assetThumbnailGenerator.CreateThumbnailAsync(sourceStream, destinationStream, ResizeOptions);
                                    destinationStream.Position = 0;
                                }

                                using (Profiler.Trace("ResizeUpload"))
                                {
                                    await assetStore.UploadAsync(resizedAsset, destinationStream);
                                    destinationStream.Position = 0;
                                }

                                await destinationStream.CopyToAsync(body, ct);
                            }
                        }
                    }
                }
            });

            return new FileCallbackResult(App.Image.MimeType, callback)
            {
                ErrorAs404 = true
            };
        }

        /// <summary>
        /// Remove the app image.
        /// </summary>
        /// <param name="app">The name of the app to update.</param>
        /// <returns>
        /// 200 => App image removed.
        /// 404 => App not found.
        /// </returns>
        [HttpDelete]
        [Route("apps/{app}/image")]
        [ProducesResponseType(typeof(AppDto), 200)]
        [ApiPermission(Permissions.AppUpdate)]
        [ApiCosts(0)]
        public async Task<IActionResult> DeleteImage(string app)
        {
            var response = await InvokeCommandAsync(new RemoveAppImage());

            return Ok(response);
        }

        /// <summary>
        /// Archive the app.
        /// </summary>
        /// <param name="app">The name of the app to archive.</param>
        /// <returns>
        /// 204 => App archived.
        /// 404 => App not found.
        /// </returns>
        [HttpDelete]
        [Route("apps/{app}/")]
        [ApiPermission(Permissions.AppDelete)]
        [ApiCosts(0)]
        public async Task<IActionResult> DeleteApp(string app)
        {
            await CommandBus.PublishAsync(new ArchiveApp());

            return NoContent();
        }

        private async Task<AppDto> InvokeCommandAsync(ICommand command)
        {
            var context = await CommandBus.PublishAsync(command);

            var userOrClientId = HttpContext.User.UserOrClientId()!;
            var userPermissions = HttpContext.Permissions();

            var result = context.Result<IAppEntity>();
            var response = AppDto.FromApp(result, userOrClientId, userPermissions, appPlansProvider, this);

            return response;
        }

        private UploadAppImage CreateCommand(IFormFile? file)
        {
            if (file == null || Request.Form.Files.Count != 1)
            {
                var error = new ValidationError($"Can only upload one file, found {Request.Form.Files.Count} files.");

                throw new ValidationException("Cannot upload image.", error);
            }

            return new UploadAppImage { File = file.ToAssetFile() };
        }

        private static FileStream GetTempStream()
        {
            var tempFileName = Path.GetTempFileName();

            return new FileStream(tempFileName,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Delete, 1024 * 16,
                FileOptions.Asynchronous |
                FileOptions.DeleteOnClose |
                FileOptions.SequentialScan);
        }
    }
}
