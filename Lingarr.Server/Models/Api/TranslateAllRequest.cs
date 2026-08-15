using System.ComponentModel.DataAnnotations;
using Lingarr.Core.Enum;

namespace Lingarr.Server.Models.Api;

public class TranslateAllRequest
{
    public required string TargetLanguage { get; set; }
    [EnumDataType(typeof(MediaType))]
    public required MediaType MediaType { get; set; }
}
