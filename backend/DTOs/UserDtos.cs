using System.ComponentModel.DataAnnotations;

namespace FactoryQuoteApi.DTOs;

public class CreateEmployeeRequestDto
{
    [Required(ErrorMessage = "الاسم بالكامل مطلوب")]
    [MaxLength(150, ErrorMessage = "الاسم لا يمكن أن يتجاوز 150 حرفاً")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "البريد الإلكتروني مطلوب")]
    [EmailAddress(ErrorMessage = "صيغة البريد الإلكتروني غير صحيحة")]
    [MaxLength(150, ErrorMessage = "البريد الإلكتروني لا يمكن أن يتجاوز 150 حرفاً")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "كلمة المرور مطلوبة")]
    [MinLength(6, ErrorMessage = "يجب أن تكون كلمة المرور 6 أحرف على الأقل")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "تأكيد كلمة المرور مطلوب")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class UpdateEmployeeRequestDto
{
    [Required(ErrorMessage = "الاسم بالكامل مطلوب")]
    [MaxLength(150, ErrorMessage = "الاسم لا يمكن أن يتجاوز 150 حرفاً")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "البريد الإلكتروني مطلوب")]
    [EmailAddress(ErrorMessage = "صيغة البريد الإلكتروني غير صحيحة")]
    [MaxLength(150, ErrorMessage = "البريد الإلكتروني لا يمكن أن يتجاوز 150 حرفاً")]
    public string Email { get; set; } = string.Empty;
}

public class ResetEmployeePasswordRequestDto
{
    [Required(ErrorMessage = "كلمة المرور الجديدة مطلوبة")]
    [MinLength(6, ErrorMessage = "يجب أن تكون كلمة المرور 6 أحرف على الأقل")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "تأكيد كلمة المرور مطلوب")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class ToggleActiveRequestDto
{
    public bool? IsActive { get; set; }
}

public class EmployeeResponseDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = "Sales";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public int QuotesCount { get; set; }
}
