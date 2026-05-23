using System.ComponentModel.DataAnnotations;
using GankedTV.Api.Validation;

namespace GankedTV.Api.Contracts.Comments;

public sealed record CreateCommentRequest(
    [property: Required]
    [property: StringLength(CommentValidationLimits.MaxBodyLength, MinimumLength = 1)]
    string? Body,
    // null = top-level comment; set = reply to a top-level comment on the same clip.
    Guid? ParentId);
