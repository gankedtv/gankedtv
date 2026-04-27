namespace GankedTV.Api.Contracts.Clips;

// ContentType: the MIME the server signed the presigned PUT for. The client MUST send
// this exact value as the request Content-Type — S3/MinIO includes it in the signature
// and will reject the upload with 403 SignatureDoesNotMatch otherwise.
public sealed record UploadUrlResponse(string Url, DateTimeOffset ExpiresAt, string ContentType);
