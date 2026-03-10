using DigitalWallet.Application.Interfaces;
using DigitalWallet.Api.Models.Requests;
using DigitalWallet.Api.Models.Responses;

namespace DigitalWallet.Application.Services;

/// <summary>
/// Stub implementation — business logic added Days 11–12.
/// </summary>
public class WalletService : IWalletService
{
    public Task<WalletResponse> CreateWalletAsync(
        string userId, CreateWalletRequest request, CancellationToken cancellationToken)
        => throw new NotImplementedException("Implemented Day 11");

    public Task<WalletResponse> GetWalletAsync(
        string walletId, string requestingUserId, CancellationToken cancellationToken)
        => throw new NotImplementedException("Implemented Day 11");

    public Task<TransactionResponse> DepositAsync(
        string walletId, string userId, string idempotencyKey,
        DepositRequest request, CancellationToken cancellationToken)
        => throw new NotImplementedException("Implemented Day 11");

    public Task<WalletDeactivatedResponse> DeactivateWalletAsync(
        string walletId, string userId, CancellationToken cancellationToken)
        => throw new NotImplementedException("Implemented Day 11");
}

/// <summary>
/// Stub implementation — atomic transfer logic added Days 11–12.
/// </summary>
public class TransferService : ITransferService
{
    public Task<TransferResponse> TransferAsync(
        string requestingUserId, string idempotencyKey,
        TransferRequest request, CancellationToken cancellationToken)
        => throw new NotImplementedException("Implemented Day 11");
}

/// <summary>
/// Stub implementation — paginated query logic added Days 11–12.
/// </summary>
public class TransactionService : ITransactionService
{
    public Task<PagedApiResponse<TransactionResponse>> GetTransactionsAsync(
        string walletId, string requestingUserId,
        GetTransactionsRequest query, CancellationToken cancellationToken)
        => throw new NotImplementedException("Implemented Day 11");
}
