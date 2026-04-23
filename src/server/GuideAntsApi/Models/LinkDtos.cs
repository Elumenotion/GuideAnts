using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.Models;

public record CreateLinkDto(
    [Required] [Url] string Url
);

public record UpdateLinkDto(
    [Required] [Url] string Url
); 