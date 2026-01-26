namespace Snowcloak.API.Dto.Files;

public class FilesSendDto
{
    public List<string> FileHashes { get; set; } = new();
    public List<string> UIDs { get; set; } = new();
}