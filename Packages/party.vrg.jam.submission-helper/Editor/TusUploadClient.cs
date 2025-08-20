using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Party.Vrg.Jam
{
    public class TusUploadClient
    {
        private const string TUS_VERSION = "1.0.0";
        private const string SERVER_URL = "https://jamuploads.vrg.party/files";
        private const int CHUNK_SIZE = 1024 * 1024; // 1MB chunks
        private const int MAX_RETRIES = 3;

        public static IEnumerator UploadFile(
            byte[] fileData,
            string filename,
            System.Action<float> onProgress = null,
            System.Action<string> onSuccess = null,
            System.Action<string> onError = null
        )
        {
            Debug.Log(
                $"[TusUploadClient] Starting upload for file: {filename}, size: {fileData.Length} bytes"
            );
            var client = new TusUploadClient();
            yield return client.StartUpload(fileData, filename, onProgress, onSuccess, onError);
        }

        private IEnumerator StartUpload(
            byte[] fileData,
            string filename,
            System.Action<float> onProgress,
            System.Action<string> onSuccess,
            System.Action<string> onError
        )
        {
            Debug.Log($"[TusUploadClient] StartUpload called for {filename}");
            onProgress?.Invoke(0f);

            // Step 1: Create upload
            Debug.Log($"[TusUploadClient] Creating upload session...");
            string uploadUrl = null;
            yield return CreateUpload(
                fileData.Length,
                filename,
                url =>
                {
                    uploadUrl = url;
                    Debug.Log($"[TusUploadClient] Upload session created: {url}");
                },
                error =>
                {
                    Debug.LogError($"[TusUploadClient] Upload creation failed: {error}");
                    onError?.Invoke(error);
                }
            );

            if (string.IsNullOrEmpty(uploadUrl))
            {
                Debug.LogError("[TusUploadClient] No upload URL received, aborting");
                yield break; // Error already reported
            }

            // Step 2: Upload file data
            Debug.Log($"[TusUploadClient] Starting data upload to {uploadUrl}");
            yield return UploadData(uploadUrl, fileData, onProgress, onSuccess, onError);
        }

        private IEnumerator CreateUpload(
            long fileSize,
            string filename,
            System.Action<string> onSuccess,
            System.Action<string> onError
        )
        {
            var encodedFilename = Convert.ToBase64String(Encoding.UTF8.GetBytes(filename));
            Debug.Log($"[TusUploadClient] Encoded filename: {encodedFilename}");

            using (var request = UnityWebRequest.PostWwwForm(SERVER_URL, ""))
            {
                // Clear default content since we don't need form data
                request.uploadHandler = new UploadHandlerRaw(new byte[0]);
                request.uploadHandler.contentType = "";

                // Set tus headers
                request.SetRequestHeader("Tus-Resumable", TUS_VERSION);
                request.SetRequestHeader("Upload-Length", fileSize.ToString());
                request.SetRequestHeader("Upload-Metadata", $"filename {encodedFilename}");

                Debug.Log($"[TusUploadClient] Sending POST to {SERVER_URL}");
                Debug.Log(
                    $"[TusUploadClient] Headers: Tus-Resumable={TUS_VERSION}, Upload-Length={fileSize}, Upload-Metadata=filename {encodedFilename}"
                );

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success && request.responseCode == 201)
                {
                    var location = request.GetResponseHeader("Location");
                    if (!string.IsNullOrEmpty(location))
                    {
                        onSuccess?.Invoke(location);
                    }
                    else
                    {
                        onError?.Invoke("Server did not return upload location");
                    }
                }
                else
                {
                    var errorMessage = "Upload creation failed";

                    // Try to parse JSON error response
                    if (!string.IsNullOrEmpty(request.downloadHandler.text))
                    {
                        try
                        {
                            var errorResponse = JsonUtility.FromJson<ErrorResponse>(
                                request.downloadHandler.text
                            );
                            if (!string.IsNullOrEmpty(errorResponse.error))
                            {
                                errorMessage = errorResponse.error;
                            }
                        }
                        catch
                        {
                            // Fallback to raw response if JSON parsing fails
                            errorMessage = request.downloadHandler.text;
                        }
                    }

                    onError?.Invoke($"{errorMessage} (HTTP {request.responseCode})");
                }
            }
        }

        private IEnumerator UploadData(
            string uploadUrl,
            byte[] fileData,
            System.Action<float> onProgress,
            System.Action<string> onSuccess,
            System.Action<string> onError
        )
        {
            long uploadedBytes = 0;
            int retryCount = 0;

            while (uploadedBytes < fileData.Length)
            {
                // Check current offset on server
                yield return CheckUploadOffset(
                    uploadUrl,
                    offset =>
                    {
                        uploadedBytes = offset;
                    },
                    error =>
                    {
                        Debug.LogWarning($"Failed to check upload offset: {error}");
                        // Continue with current offset
                    }
                );

                if (uploadedBytes >= fileData.Length)
                {
                    break; // Upload complete
                }

                // Calculate chunk size
                var remainingBytes = fileData.Length - uploadedBytes;
                var chunkSize = Math.Min(CHUNK_SIZE, remainingBytes);

                // Prepare chunk data
                var chunkData = new byte[chunkSize];
                Array.Copy(fileData, uploadedBytes, chunkData, 0, chunkSize);

                // Upload chunk
                bool chunkSuccess = false;
                yield return UploadChunk(
                    uploadUrl,
                    chunkData,
                    uploadedBytes,
                    newOffset =>
                    {
                        uploadedBytes = newOffset;
                        chunkSuccess = true;
                        retryCount = 0; // Reset retry count on success
                        onProgress?.Invoke((float)uploadedBytes / fileData.Length);
                    },
                    error =>
                    {
                        Debug.LogWarning($"Chunk upload failed: {error}");
                        retryCount++;
                    }
                );

                if (!chunkSuccess)
                {
                    if (retryCount >= MAX_RETRIES)
                    {
                        onError?.Invoke($"Upload failed after {MAX_RETRIES} retries");
                        yield break;
                    }

                    // Wait before retry
                    yield return new WaitForSeconds(1f * retryCount);
                }
            }

            // Upload complete
            onSuccess?.Invoke(uploadUrl);
        }

        private IEnumerator CheckUploadOffset(
            string uploadUrl,
            System.Action<long> onSuccess,
            System.Action<string> onError
        )
        {
            using (var request = UnityWebRequest.Head(uploadUrl))
            {
                request.SetRequestHeader("Tus-Resumable", TUS_VERSION);

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var offsetHeader = request.GetResponseHeader("Upload-Offset");
                    if (long.TryParse(offsetHeader, out long offset))
                    {
                        onSuccess?.Invoke(offset);
                    }
                    else
                    {
                        onError?.Invoke("Invalid upload offset from server");
                    }
                }
                else
                {
                    onError?.Invoke($"Failed to check upload offset: {request.error}");
                }
            }
        }

        private IEnumerator UploadChunk(
            string uploadUrl,
            byte[] chunkData,
            long offset,
            System.Action<long> onSuccess,
            System.Action<string> onError
        )
        {
            using (var request = new UnityWebRequest(uploadUrl, "PATCH"))
            {
                request.uploadHandler = new UploadHandlerRaw(chunkData);
                request.downloadHandler = new DownloadHandlerBuffer();

                request.SetRequestHeader("Tus-Resumable", TUS_VERSION);
                request.SetRequestHeader("Content-Type", "application/offset+octet-stream");
                request.SetRequestHeader("Upload-Offset", offset.ToString());

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success && request.responseCode == 204)
                {
                    var newOffsetHeader = request.GetResponseHeader("Upload-Offset");
                    if (long.TryParse(newOffsetHeader, out long newOffset))
                    {
                        onSuccess?.Invoke(newOffset);
                    }
                    else
                    {
                        onError?.Invoke("Server did not return valid upload offset");
                    }
                }
                else
                {
                    onError?.Invoke(
                        $"Chunk upload failed: {request.error} (HTTP {request.responseCode})"
                    );
                }
            }
        }

        [System.Serializable]
        private class ErrorResponse
        {
            public string error;
        }
    }
}
