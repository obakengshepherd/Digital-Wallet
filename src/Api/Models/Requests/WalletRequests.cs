using System.ComponentModel.DataAnnotations;

namespace DigitalWallet.Api.Models.Requests;

public record CreateWalletRequest
{
    [Required]
    [StringLength(3, MinimumLength = 3)]
    [RegularExpression(@"^[A-Z]{3}$", ErrorMessage = "Currency must be a 3-letter ISO 4217 code in uppercase.")]
    public string Currency { get; init; } = string.Empty;
}

public record DepositRequest
{
    [Required]
    [Range(typeof(decimal), "0.01", "9999999.99", ErrorMessage = "Amount must be greater than zero.")]
    public decimal Amount { get; init; }

    [StringLength(128)]
    public string? Reference { get; init; }
}

public record TransferRequest
{
    [Required]
    public string SourceWalletId { get; init; } = string.Empty;

    [Required]
    public string DestinationWalletId { get; init; } = string.Empty;

    [Required]
    [Range(typeof(decimal), "0.01", "9999999.99", ErrorMessage = "Amount must be greater than zero.")]
    public decimal Amount { get; init; }

    [StringLength(256)]
    public string? Note { get; init; }
}

public record GetTransactionsRequest
{
    [Range(1, 100)]
    public int Limit { get; init; } = 20;

    public string? Cursor { get; init; }

    public string? Type { get; init; } // CREDIT | DEBIT
}
