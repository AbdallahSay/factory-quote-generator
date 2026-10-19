using System.ComponentModel.DataAnnotations;

namespace FactoryQuoteApi.DTOs;

public class CreateTelegramRecipientRequestDto
{
    [Required(ErrorMessage = "معرف المحادثة (Chat ID) مطلوب")]
    [MaxLength(100, ErrorMessage = "معرف المحادثة لا يمكن أن يتجاوز 100 حرف")]
    public string ChatId { get; set; } = string.Empty;

    [Required(ErrorMessage = "الاسم التعريفي للمستلم مطلوب")]
    [MaxLength(150, ErrorMessage = "الاسم التعريفي لا يمكن أن يتجاوز 150 حرفاً")]
    public string DisplayName { get; set; } = string.Empty;
}

public class UpdateTelegramRecipientRequestDto
{
    [Required(ErrorMessage = "الاسم التعريفي للمستلم مطلوب")]
    [MaxLength(150, ErrorMessage = "الاسم التعريفي لا يمكن أن يتجاوز 150 حرفاً")]
    public string DisplayName { get; set; } = string.Empty;

    public bool? IsActive { get; set; }
}

public class TelegramRecipientResponseDto
{
    public int Id { get; set; }
    public string ChatId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TotalSent { get; set; }
    public int TotalFailed { get; set; }
    public DateTime? LastDeliveryAt { get; set; }
    public string? LastStatus { get; set; }
}

public class RetryTelegramRequestDto
{
    public string? ChatId { get; set; }
}
