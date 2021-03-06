﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Routing;
using Kudu.Contracts.Editor;
using Kudu.Contracts.Tracing;
using Kudu.Core;

namespace Kudu.Services.Infrastructure
{
    /// <summary>
    /// Provides common functionality for Virtual File System controllers.
    /// </summary>
    public abstract class VfsControllerBase : ApiController
    {
        private const string DirectoryEnumerationSearchPattern = "*";
        private const char UriSegmentSeparator = '/';

        private static readonly char[] _uriSegmentSeparator = new char[] { UriSegmentSeparator };
        private static readonly MediaTypeHeaderValue _directoryMediaType = MediaTypeHeaderValue.Parse("inode/directory");

        protected const int BufferSize = 32 * 1024;

        protected VfsControllerBase(ITracer tracer, IEnvironment environment, string rootPath)
        {
            if (rootPath == null)
            {
                throw new ArgumentNullException("rootPath");
            }
            Tracer = tracer;
            Environment = environment;
            RootPath = Path.GetFullPath(rootPath.TrimEnd(Path.DirectorySeparatorChar));
            MediaTypeMap = new MediaTypeMap();
        }

        [AcceptVerbs("GET", "HEAD")]
        public virtual Task<HttpResponseMessage> GetItem()
        {
            string localFilePath = GetLocalFilePath();
            DirectoryInfo info = new DirectoryInfo(localFilePath);

            if (info.Attributes < 0)
            {
                HttpResponseMessage notFoundResponse = Request.CreateResponse(HttpStatusCode.NotFound);
                return Task.FromResult(notFoundResponse);
            }
            else if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                // If request URI does NOT end in a "/" then redirect to one that does
                if (localFilePath[localFilePath.Length - 1] != Path.DirectorySeparatorChar)
                {
                    HttpResponseMessage redirectResponse = Request.CreateResponse(HttpStatusCode.TemporaryRedirect);
                    UriBuilder location = new UriBuilder(Request.RequestUri);
                    location.Path += "/";
                    redirectResponse.Headers.Location = location.Uri;
                    return Task.FromResult(redirectResponse);
                }
                else
                {
                    return CreateDirectoryGetResponse(info, localFilePath);
                }
            }
            else
            {
                // If request URI ends in a "/" then redirect to one that does not
                if (localFilePath[localFilePath.Length - 1] == Path.DirectorySeparatorChar)
                {
                    HttpResponseMessage redirectResponse = Request.CreateResponse(HttpStatusCode.TemporaryRedirect);
                    UriBuilder location = new UriBuilder(Request.RequestUri);
                    location.Path = location.Path.TrimEnd(_uriSegmentSeparator);
                    redirectResponse.Headers.Location = location.Uri;
                    return Task.FromResult(redirectResponse);
                }

                // We are ready to get the file
                return CreateItemGetResponse(info, localFilePath);
            }
        }

        [HttpPut]
        public virtual Task<HttpResponseMessage> PutItem()
        {
            string localFilePath = GetLocalFilePath();
            DirectoryInfo info = new DirectoryInfo(localFilePath);
            bool itemExists = info.Attributes >= 0;

            if (itemExists && (info.Attributes & FileAttributes.Directory) != 0)
            {
                return CreateDirectoryPutResponse(info, localFilePath);
            }
            else
            {
                // If request URI ends in a "/" then redirect to one that does not
                if (localFilePath[localFilePath.Length - 1] == Path.DirectorySeparatorChar)
                {
                    HttpResponseMessage redirectResponse = Request.CreateResponse(HttpStatusCode.TemporaryRedirect);
                    UriBuilder location = new UriBuilder(Request.RequestUri);
                    location.Path = location.Path.TrimEnd(_uriSegmentSeparator);
                    redirectResponse.Headers.Location = location.Uri;
                    return Task.FromResult(redirectResponse);
                }

                // We are ready to update the file
                return CreateItemPutResponse(info, localFilePath, itemExists);
            }
        }

        [HttpDelete]
        public virtual Task<HttpResponseMessage> DeleteItem()
        {
            string localFilePath = GetLocalFilePath();
            DirectoryInfo info = new DirectoryInfo(localFilePath);

            if (info.Attributes < 0)
            {
                HttpResponseMessage notFoundResponse = Request.CreateResponse(HttpStatusCode.NotFound);
                return Task.FromResult(notFoundResponse);
            }
            else if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                try
                {
                    info.Delete();
                }
                catch (Exception ex)
                {
                    Tracer.TraceError(ex);
                    HttpResponseMessage conflictDirectoryResponse = Request.CreateErrorResponse(
                        HttpStatusCode.Conflict, Resources.VfsControllerBase_CannotDeleteDirectory);
                    return Task.FromResult(conflictDirectoryResponse);
                }

                // Delete directory succeeded.
                HttpResponseMessage successResponse = Request.CreateResponse(HttpStatusCode.OK);
                return Task.FromResult(successResponse);
            }
            else
            {
                // If request URI ends in a "/" then redirect to one that does not
                if (localFilePath[localFilePath.Length - 1] == Path.DirectorySeparatorChar)
                {
                    HttpResponseMessage redirectResponse = Request.CreateResponse(HttpStatusCode.TemporaryRedirect);
                    UriBuilder location = new UriBuilder(Request.RequestUri);
                    location.Path = location.Path.TrimEnd(_uriSegmentSeparator);
                    redirectResponse.Headers.Location = location.Uri;
                    return Task.FromResult(redirectResponse);
                }

                // We are ready to delete the file
                return CreateItemDeleteResponse(info, localFilePath);
            }
        }

        protected ITracer Tracer { get; private set; }

        protected IEnvironment Environment { get; private set; }

        protected string RootPath { get; private set; }

        protected MediaTypeMap MediaTypeMap { get; private set; }

        protected virtual Task<HttpResponseMessage> CreateDirectoryGetResponse(DirectoryInfo info, string localFilePath)
        {
            // Enumerate directory
            IEnumerable<VfsStatEntry> directory = GetDirectoryResponse(info, localFilePath);
            HttpResponseMessage successDirectoryResponse = Request.CreateResponse<IEnumerable<VfsStatEntry>>(HttpStatusCode.OK, directory);
            return Task.FromResult(successDirectoryResponse);
        }

        protected abstract Task<HttpResponseMessage> CreateItemGetResponse(FileSystemInfo info, string localFilePath);

        protected virtual Task<HttpResponseMessage> CreateDirectoryPutResponse(DirectoryInfo info, string localFilePath)
        {
            HttpResponseMessage conflictDirectoryResponse = Request.CreateErrorResponse(
                HttpStatusCode.Conflict, Resources.VfsController_CannotUpdateDirectory);
            return Task.FromResult(conflictDirectoryResponse);
        }

        protected abstract Task<HttpResponseMessage> CreateItemPutResponse(FileSystemInfo info, string localFilePath, bool itemExists);

        protected virtual Task<HttpResponseMessage> CreateItemDeleteResponse(FileSystemInfo info, string localFilePath)
        {
            // Generate file response
            Stream fileStream = null;
            try
            {
                fileStream = GetFileDeleteStream(localFilePath, validate: info);
                File.Delete(localFilePath);
                HttpResponseMessage successResponse = Request.CreateResponse(HttpStatusCode.OK);
                return Task.FromResult(successResponse);
            }
            catch (Exception e)
            {
                // Could not delete the file
                Tracer.TraceError(e);
                HttpResponseMessage notFoundResponse = Request.CreateErrorResponse(HttpStatusCode.NotFound, e);
                return Task.FromResult(notFoundResponse);
            }
            finally
            {
                if (fileStream != null)
                {
                    fileStream.Close();
                }
            }
        }

        /// <summary>
        /// Indicates whether this is a conditional range request containing an
        /// If-Range header with a matching etag and a Range header indicating the 
        /// desired ranges
        /// </summary>
        protected bool IsRangeRequest(EntityTagHeaderValue currentEtag)
        {
            if (currentEtag != null && Request.Headers.IfRange != null && Request.Headers.Range != null)
            {
                // First check that the etag matches so that we can consider the range request
                if (currentEtag.Equals(Request.Headers.IfRange.EntityTag))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Indicates whether this is a If-None-Match request with a matching etag.
        /// </summary>
        protected bool IsIfNoneMatchRequest(EntityTagHeaderValue currentEtag)
        {
            return currentEtag != null && Request.Headers.IfNoneMatch != null &&
                Request.Headers.IfNoneMatch.Any(entityTag => currentEtag.Equals(entityTag));
        }

        /// <summary>
        /// Provides a common way for opening a file stream for shared reading from a file. In addition,
        /// if <paramref name="validate"/> is passed then validate that the file has not changed since the 
        /// <see cref="FileSystemInfo"/> was obtained. This check can prevent a class of race conditions where
        /// another request comes in and changes the file between when the <see cref="FileSystemInfo"/> was
        /// obtained and the file opened exclusively.
        /// </summary>
        protected Stream GetFileReadStream(string localFilePath, FileSystemInfo validate = null)
        {
            Contract.Assert(localFilePath != null);

            // Open file exclusively for read-sharing
            FileStream fStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            if (validate != null)
            {
                ValidateFileSystemInfo(localFilePath, validate);
            }
            return fStream;
        }

        /// <summary>
        /// Provides a common way for opening a file stream for writing exclusively to a file. In addition,
        /// if <paramref name="validate"/> is passed then validate that the file has not changed since the 
        /// <see cref="FileSystemInfo"/> was obtained. This check can prevent a class of race conditions where
        /// another request comes in and changes the file between when the <see cref="FileSystemInfo"/> was
        /// obtained and the file opened exclusively.
        /// </summary>
        protected Stream GetFileWriteStream(string localFilePath, bool fileExists, FileSystemInfo validate = null)
        {
            Contract.Assert(localFilePath != null);

            // Create path if item doesn't already exist
            if (!fileExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localFilePath));
            }

            // Open file exclusively for write without any sharing
            FileStream fStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            if (fileExists && validate != null)
            {
                ValidateFileSystemInfo(localFilePath, validate);
            }
            return fStream;
        }

        /// <summary>
        /// Provides a common way for opening a file stream for exclusively deleting the file. While 
        /// creating a stream may seem like a strange way to preparing a file for deletion, it allows us 
        /// to conclusively verify that a file hasn't changed and so it avoids certain race conditions.
        /// The stream is never actually used other than for "locking" the file.
        /// </summary>
        private Stream GetFileDeleteStream(string localFilePath, FileSystemInfo validate = null)
        {
            Contract.Assert(localFilePath != null);

            // Open file exclusively for delete sharing only
            FileStream fStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Delete);
            if (validate != null)
            {
                ValidateFileSystemInfo(localFilePath, validate);
            }
            return fStream;
        }

        private void ValidateFileSystemInfo(string localFilePath, FileSystemInfo validate)
        {
            Contract.Assert(localFilePath != null);
            Contract.Assert(validate != null);
            FileSystemInfo fInfo = new FileInfo(localFilePath);
            if (fInfo.CreationTimeUtc != validate.CreationTimeUtc || fInfo.LastWriteTimeUtc != fInfo.LastWriteTimeUtc)
            {
                throw new InvalidOperationException(Resources.VfsController_fileChanged);
            }
        }

        private string GetLocalFilePath()
        {
            IHttpRouteData routeData = Request.GetRouteData();
            string result = RootPath;
            if (routeData != null)
            {
                string path = routeData.Values["path"] as string;
                if (!String.IsNullOrEmpty(path))
                {
                    result = Path.GetFullPath(Path.Combine(result, path));
                }
                else
                {
                    string reqUri = Request.RequestUri.AbsoluteUri;
                    if (reqUri[reqUri.Length - 1] == UriSegmentSeparator)
                    {
                        result = Path.GetFullPath(result + Path.DirectorySeparatorChar);
                    }
                }
            }
            return result;
        }

        private IEnumerable<VfsStatEntry> GetDirectoryResponse(DirectoryInfo info, string localFilePath)
        {
            Contract.Assert(info != null);
            Contract.Assert(localFilePath != null);

            string baseAddress = Request.RequestUri.AbsoluteUri;
            foreach (FileSystemInfo fileSysInfo in info.EnumerateFileSystemInfos(DirectoryEnumerationSearchPattern, SearchOption.TopDirectoryOnly))
            {
                FileInfo fInfo = fileSysInfo as FileInfo;
                bool isDirectory = (fileSysInfo.Attributes & FileAttributes.Directory) != 0;
                string mime = isDirectory ? _directoryMediaType.ToString() : MediaTypeMap.GetMediaType(fileSysInfo.Extension).ToString();
                string unescapedHref = isDirectory ? fileSysInfo.Name + UriSegmentSeparator : fileSysInfo.Name;
                yield return new VfsStatEntry
                {
                    Name = fileSysInfo.Name,
                    MTime = fileSysInfo.LastWriteTimeUtc,
                    Mime = mime,
                    Size = fInfo != null ? fInfo.Length : 0,
                    Href = baseAddress + Uri.EscapeUriString(unescapedHref),
                };
            }
        }
    }
}
