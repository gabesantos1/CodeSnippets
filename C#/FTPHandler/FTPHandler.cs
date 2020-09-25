using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.StaticFiles;

namespace Namespace
{
    /// <summary>
    /// List result containing next page and a collection of <see cref="FileInfoResult"/>.
    /// </summary>
    public sealed class ListFileInfoResult
    {
        public object NextPageObject { get; set; }
        public ICollection<FileInfoResult> Files { get; set; } = new List<FileInfoResult>();
    }

    /// <summary>
    /// Basic file info.
    /// </summary>
    public sealed class FileInfoResult
    {
        public string Id { get; set; }
        public string MimeType { get; set; }
        public string Name { get; set; }
        public DateTimeOffset? CreatedTime { get; set; }
        public long Size { get; set; }
        public object Additional { get; set; }
    }
    /// <summary>
    /// Structured FTP settings information.
    /// </summary>
    /// <summary>
    /// Structured FTP settings information.
    /// </summary>
    public class FtpSettings
    {
        public string Url { get; set; }
        public string User { get; set; }
        public string Password { get; set; }

        /// <summary>
        /// Checks if <see cref="Url"/> and <see cref="Password"/> are available.
        /// </summary>
        /// <returns>true if available, otherwise false</returns>
        public bool IsCredentialsAvailable => !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password);
    }

    /// <summary>
    /// Class responsible for handling FTP methods.
    /// </summary>
    public class FtpHandler : IFileHandler
    {
        private FtpSettings _settings;
        private NetworkCredential _credentials;

        /// <inheritdoc />
        public async Task<bool> CanConnect()
        {
            var uri = new Uri(_settings.Url);
            var request = (FtpWebRequest)WebRequest.Create(uri);
            request.KeepAlive = false;
            request.Method = WebRequestMethods.Ftp.PrintWorkingDirectory;

            if (_settings.IsCredentialsAvailable)
                request.Credentials = _credentials;

            try
            {
                using (var response = await request.GetResponseAsync())
                    return true;
            }
            catch { return false; }
            finally { request.Abort(); }
        }

        /// <inheritdoc />
        /// <remarks><typeparamref name="T"/> is expected to be <see cref="FtpSettings"/></remarks>
        /// <exception cref="InvalidCastException">Settings is not a valid type.</exception>
        /// <exception cref="MissingFieldException"><see cref="FtpSettings.Url"/> is missing.</exception>
        public void LoadSettings<T>(T settings)
        {
            if (!(settings is FtpSettings))
                throw new InvalidCastException(message: "Invalid settings.");

            FtpSettings ftpSettings = settings as FtpSettings;

            if (string.IsNullOrWhiteSpace(ftpSettings.Url))
                throw new MissingFieldException(message: $"{nameof(ftpSettings.Url)} must not be null.");

            _settings = ftpSettings;
            _credentials = new NetworkCredential(_settings.User, _settings.Password);
        }

        /// <inheritdoc />
        /// <remarks>The param <paramref name="fileId"/> is not used on FTP.</remarks>
        /// <param name="fileId">Not used on FTP.</param>
        /// <param name="fileName">The file name to look up.</param>
        /// <param name="relativePath">Path relative to the <see cref="FtpSettings.Url"/>.</param>
        /// <exception cref="WebException">Fail when connecting to the ftp server.</exception>
        /// <exception cref="UnauthorizedAccessException">Unauthorized: invalid credentials.</exception>
        public async Task<FileInfoResult> FileLookupAsync(
            string fileId = "", string fileName = "",
            string relativePath = "")
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentNullException(paramName: nameof(fileName), message: "File name must be specified.");

            if (!await PathExistsAsync(fileName))
                throw new FileNotFoundException("File not found.");

            var uri = new Uri(_settings.Url);

            if (string.IsNullOrWhiteSpace(relativePath))
                uri = new Uri(uri, Path.Join(relativePath, fileName));
            else
                uri = new Uri(uri, fileName);

            try
            {
                var fileSize = await FileSize(uri);
                var fileCreatedAt = await FileTimeStamp(uri);

                string contentType = "application/octet-stream";
                new FileExtensionContentTypeProvider().TryGetContentType(fileName, out contentType);

                return new FileInfoResult
                {
                    Name = fileName,
                    CreatedTime = fileCreatedAt,
                    Size = fileSize,
                    MimeType = contentType
                };
            }
            catch (WebException e)
            when (e.Message.Contains("530"))
            {
                throw new UnauthorizedAccessException("Unauthorized: invalid credentials.");
            }
            catch (Exception e)
            {
                throw new WebException("Fail when connecting to the ftp server.", e);
            }
        }

        private async Task<DateTime> FileTimeStamp(Uri uri)
        {
            var req = (FtpWebRequest)WebRequest.Create(uri);
            if (_settings.IsCredentialsAvailable)
                req.Credentials = _credentials;
            req.Method = WebRequestMethods.Ftp.GetDateTimestamp;
            try
            {
                using (var response = (FtpWebResponse)await req.GetResponseAsync())
                {
                    return response.LastModified;
                }
            }
            finally
            {
                req.Abort();
            }
        }

        private async Task<long> FileSize(Uri uri)
        {
            var req = (FtpWebRequest)WebRequest.Create(uri);
            if (_settings.IsCredentialsAvailable)
                req.Credentials = _credentials;
            req.Method = WebRequestMethods.Ftp.GetFileSize;
            try
            {
                using (var response = await req.GetResponseAsync())
                {
                    return response.ContentLength;
                }
            }
            finally
            {
                req.Abort();
            }
        }

        /// <summary>
        /// List all directories/files from a <see cref="FtpSettings.Url"/> in a <paramref name="relativeDirectory"/>.
        /// </summary>
        /// <remarks>
        /// <para><paramref name="relativeDirectory"/> is optional, if is not specified lists in the root <see cref="FtpSettings.Url"/>.</para>
        /// <para><paramref name="relativeDirectory"/> can also be specified in <see cref="FtpSettings.Url"/>.</para>
        /// </remarks>
        /// <param name="relativeDirectory">Optional: relative directory path.</param>
        /// <param name="pageSize">Not used.</param>
        /// <param name="pageObject">Not used.</param>
        /// <returns>A <see cref="System.Collections.IEnumerable"/> of string listing all directories and files.</returns>
        /// <exception cref="WebException">Fail when connecting to the ftp server.</exception>
        /// <exception cref="UnauthorizedAccessException">Unauthorized: invalid credentials.</exception>
        public async Task<ListFileInfoResult> ListAsync(
        string relativeDirectory = "",
        int pageSize = 100, object pageObject = default)
        {
            var uri = new Uri(_settings.Url);
            if (!string.IsNullOrWhiteSpace(relativeDirectory))
                uri = new Uri(uri, relativeDirectory);

            var request = (FtpWebRequest)WebRequest.Create(uri);
            try
            {
                request.Method = WebRequestMethods.Ftp.ListDirectoryDetails;
                if (_settings.IsCredentialsAvailable)
                    request.Credentials = _credentials;

                using (var response = (FtpWebResponse)await request.GetResponseAsync())
                using (var responseStream = response.GetResponseStream())
                {
                    using (var reader = new StreamReader(responseStream))
                    {
                        var filesList = new ListFileInfoResult();
                        while (reader?.EndOfStream == false)
                        {
                            var filesInfo = new FileInfoResult
                            {
                                Additional = reader.ReadLine(),
                            };
                            var name = ((string)filesInfo.Additional).Split(" ").Last();
                            if (name == "." || name == "..")
                                continue;
                            filesInfo.Name = name;
                            filesList.Files.Add(filesInfo);
                        }

                        return filesList;
                    }
                }
            }
            // 530 means not logged in
            catch (WebException e)
            when (e.Message.Contains("530"))
            {
                throw new UnauthorizedAccessException("Unauthorized: invalid credentials.");
            }
            catch (Exception e)
            {
                throw new WebException("Fail when connecting to the ftp server.", e);
            }
            finally
            {
                request.Abort();
            }
        }

        /// <inheritdoc />
        /// <param name="filePath">The file path to be moved.</param>
        /// <param name="pathToMove">The path for the file to be moved to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="filePath"/> or <paramref name="pathToMove"/> are null.</exception>
        /// <exception cref="FileNotFoundException">File in <paramref name="filePath"/> not found.</exception>
        /// <exception cref="DuplicateNameException"><paramref name="pathToMove"/> destination path already exists.</exception>
        /// <exception cref="UnauthorizedAccessException">Invalid credentials.</exception>
        public async Task MoveFileAsync(string filePath, string pathToMove)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(paramName: nameof(filePath), message: "File path must not be null.");
            if (string.IsNullOrWhiteSpace(pathToMove))
                throw new ArgumentNullException(paramName: nameof(pathToMove), message: "Path to move must not be null.");

            var location = new Uri(Path.Join(_settings.Url, filePath));
            var destination = new Uri(Path.Join(_settings.Url, pathToMove));

            if (!await PathExistsAsync(filePath))
                throw new FileNotFoundException(message: "File not found.", fileName: filePath);
            if (await PathExistsAsync(pathToMove))
                throw new DuplicateNameException("Destination already exists.");

            var targetUriRelative = location.MakeRelativeUri(destination);

            var request = (FtpWebRequest)WebRequest.Create(location);

            try
            {
                request.Method = WebRequestMethods.Ftp.Rename;

                if (_settings.IsCredentialsAvailable)
                    request.Credentials = _credentials;
                request.RenameTo = Uri.UnescapeDataString(targetUriRelative.OriginalString);
                using (var response = (FtpWebResponse)await request.GetResponseAsync())
                    return;
            }
            catch (WebException e)
            when (e.Message.Contains("530"))
            {
                throw new UnauthorizedAccessException("Unauthorized: invalid credentials.");
            }
            catch (Exception e)
            {
                throw new WebException("Fail when connecting to the ftp server.", e);
            }
            finally
            {
                request.Abort();
            }
        }

        /// <summary>
        /// Download a file from a <see cref="FtpSettings.Url"/> location.
        /// </summary>
        /// <remarks>
        /// <para><paramref name="fileLocation"/> use as a relative path location to <see cref="FtpSettings.Url"/>.</para>
        /// <para><paramref name="fileLocation"/> can also be specified in <see cref="FtpSettings.Url"/>.</para>
        /// </remarks>
        /// <param name="fileResult">Stream to return the file.</param>
        /// <param name="fileLocation">Optional: relative path file location.</param>
        /// <returns>The file as a <see cref="Stream"/>.</returns>
        /// <exception cref="WebException">Fail when connecting to the ftp server.</exception>
        /// <exception cref="UnauthorizedAccessException">Unauthorized: invalid credentials.</exception>
        public async Task DownloadFileAsync(Stream fileResult, string fileLocation)
        {
            var uri = new Uri(_settings.Url);
            if (!string.IsNullOrWhiteSpace(fileLocation))
                uri = new Uri(uri, fileLocation);

            var request = (FtpWebRequest)WebRequest.Create(uri);
            try
            {
                request.Method = WebRequestMethods.Ftp.DownloadFile;
                if (_settings.IsCredentialsAvailable)
                    request.Credentials = _credentials;

                using (var response = (FtpWebResponse)await request.GetResponseAsync())
                using (var respStream = response.GetResponseStream())
                {
                    respStream.CopyTo(fileResult);
                }
            }
            // 530 means not logged in
            catch (WebException e)
            when (e.Message.Contains("530"))
            {
                throw new UnauthorizedAccessException("Unauthorized: invalid credentials.");
            }
            catch (Exception e)
            {
                throw new WebException("Fail when connecting to the ftp server.", e);
            }
        }

        /// <summary>
        /// Upload a file into the requested <see cref="FtpSettings.Url"/>
        /// </summary>
        /// <param name="file">A file as a <see cref="Stream"/>.</param>
        /// <param name="fileName">The file name.</param>
        /// <param name="relativeUploadPath">Upload location relative to <see cref="FtpSettings.Url"/>.</param>
        /// <returns>true if the file was uploaded, otherwise false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
        /// <exception cref="WebException">Fail when connecting to the ftp server.</exception>
        /// <exception cref="UnauthorizedAccessException">Unauthorized: invalid credentials.</exception>
        public async Task UploadFileAsync(
            Stream file, string fileName,
            string relativeUploadPath)
        {
            if (file?.Length == 0)
                throw new ArgumentNullException(paramName: nameof(file), message: "File must not be null.");

            var uri = new Uri(_settings.Url);
            Uri uploadLocationUri = null;

            if (!string.IsNullOrWhiteSpace(relativeUploadPath))
            {
                uploadLocationUri = new Uri(uri, relativeUploadPath);

                if (!string.IsNullOrWhiteSpace(fileName))
                    uri = new Uri(uri, Path.Join(relativeUploadPath, fileName));
                else
                    uri = uploadLocationUri;
            }
            else if (!string.IsNullOrWhiteSpace(fileName))
            {
                uri = new Uri(uri, fileName);
            }

            if (uploadLocationUri != null
                && !await PathExistsAsync(uploadLocationUri.AbsolutePath))
            {
                await CreateDirectoryAsync(relativeUploadPath);
            }

            var uploadRequest = (FtpWebRequest)WebRequest.Create(uri);

            try
            {
                uploadRequest.Method = WebRequestMethods.Ftp.UploadFile;
                if (_settings.IsCredentialsAvailable)
                    uploadRequest.Credentials = _credentials;

                uploadRequest.ContentLength = file.Length;
                using (var requestStream = await uploadRequest.GetRequestStreamAsync())
                {
                    file.CopyTo(requestStream);
                }

                using (var response = await uploadRequest.GetResponseAsync() as FtpWebResponse)
                    return;
            }
            // 530 means not logged in
            catch (WebException e)
            when (e.Message.Contains("530"))
            {
                throw new UnauthorizedAccessException("Unauthorized: invalid credentials.");
            }
            catch (Exception e)
            {
                throw new WebException("Fail when connecting to the ftp server..", e);
            }
            finally
            {
                uploadRequest.Abort();
            }
        }

        /// <summary>
        /// Creates a directory into the <see cref="FtpSettings.Url"/> using specified <paramref name="relativePath"/>.
        /// </summary>
        /// <param name="relativePath">Relative pah to <see cref="FtpSettings.Url"/></param>
        /// <returns>true if directory created, otherwise false</returns>
        /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is null.</exception>
        /// <exception cref="UnauthorizedAccessException">Unauthorized: invalid credentials.</exception>
        /// <exception cref="WebException">Fail when connecting to the ftp server.</exception>
        public async Task CreateDirectoryAsync(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentNullException(paramName: nameof(relativePath), message: "Relative path must not be null.");

            var uri = new Uri(_settings.Url);

            var paths = relativePath.Split('/').ToList();
            paths.RemoveAll(string.IsNullOrWhiteSpace);

            try
            {
                string lastPath = string.Empty;
                for (int i = 0; i < paths.Count; i++)
                {
                    lastPath += paths[i] + "/";
                    if (await PathExistsAsync(Path.Join(uri.AbsolutePath, lastPath)))
                        continue;

                    var request = (FtpWebRequest)WebRequest.Create(new Uri(uri, lastPath));
                    request.Method = WebRequestMethods.Ftp.MakeDirectory;

                    if (_settings.IsCredentialsAvailable)
                        request.Credentials = _credentials;

                    using (var response = await request.GetResponseAsync() as FtpWebResponse)
                    using (var responseStream = response.GetResponseStream())
                        request.Abort();
                }
            }
            // 530 means not logged in
            catch (WebException e)
            when (e.Message.Contains("530"))
            {
                throw new UnauthorizedAccessException("Unauthorized: invalid credentials.");
            }
            catch (Exception e)
            {
                throw new WebException("Fail when connecting to the ftp server.", e);
            }
        }

        /// <summary>
        /// Check if the specified <paramref name="path"/> exists.
        /// </summary>
        /// <param name="path">Relative path to the ftp url.</param>
        /// <returns>true if exists, otherwise false</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="TimeoutException">Connection timed out.</exception>
        /// <exception cref="UnauthorizedAccessException">Unauthorized: invalid credentials.</exception>
        public async Task<bool> PathExistsAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(paramName: nameof(path), message: "Path must not be null.");

            var uri = new Uri(Path.Join(_settings.Url, path));
            var request = (FtpWebRequest)WebRequest.Create(uri);

            try
            {
                if (_settings.IsCredentialsAvailable)
                    request.Credentials = _credentials;

                var fileExt = Path.GetExtension(uri.AbsoluteUri.Split('/').Last());
                bool isDirectory = string.IsNullOrWhiteSpace(fileExt);

                request.Method = isDirectory ?
                    WebRequestMethods.Ftp.ListDirectory
                    : WebRequestMethods.Ftp.GetFileSize;

                using (var response = await request.GetResponseAsync() as FtpWebResponse)
                    return true;
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (WebException e)
            when (e.Message.Contains("530"))
            {
                throw new UnauthorizedAccessException("Unauthorized: invalid credentials.");
            }
            catch
            {
                return false;
            }
            finally
            {
                request.Abort();
            }
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentNullException">File name is null.</exception>
        /// <exception cref="TimeoutException">Connection timed out.</exception>
        /// <exception cref="UnauthorizedAccessException">Invalid credentials.</exception>
        public async Task DeleteFileAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentNullException(paramName: nameof(fileName), message: "File name must not be null.");
            var uri = new Uri(Path.Join(_settings.Url, fileName));

            var request = (FtpWebRequest)WebRequest.Create(uri);
            if (_settings.IsCredentialsAvailable)
                request.Credentials = _credentials;

            try
            {
                request.Method = WebRequestMethods.Ftp.DeleteFile;
                using (var response = await request.GetResponseAsync() as FtpWebResponse)
                    return;
            }
            catch (WebException e)
            when (e.Message.Contains("530"))
            {
                throw new UnauthorizedAccessException("Unauthorized: invalid credentials.");
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new WebException("Fail when connecting to the ftp server.", e);
            }
            finally
            {
                request.Abort();
            }
        }
    }
}
