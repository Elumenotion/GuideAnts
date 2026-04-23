using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.Models;

public record ContentRequestDto(
    [Required] [Url] string Url
);

public record ContentResponseDto(
    string Content
); 
