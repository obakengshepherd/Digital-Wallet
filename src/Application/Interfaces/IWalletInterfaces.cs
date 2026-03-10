using DigitalWallet.Api.Models.Requests;
using DigitalWallet.Api.Models.Responses;

namespace DigitalWallet.Application.Interfaces;

/// <summary>
/// Manages wallet lifecycle: creation, balance reads, deposits, and deactivation.
/// Does not handle transfers — see ITransferService.
/// </summary>
public interface IWalletService
{
    /// <summary>
    /// Creates a new wallet for the given user in the specified currency.
    /// Throws DuplicateWalletException if the user already has a wallet in this currency.
    /// </summary>
    Task<WalletResponse> CreateWalletAsync(
        string userId,
        CreateWalletRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves the current wallet state including the live balance.
    /// Checks Redis cache first; falls back to PostgreSQL on miss.
    /// Throws WalletNotFoundException if not found.
    /// Throws WalletAccessDeniedException if the wallet belongs to a different user.
    /// </summary>
    Task<WalletResponse> GetWalletAsync(
        string walletId,
        string requestingUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Credits the wallet with the specified amount.
    /// Idempotency is enforced by the idempotencyKey parameter.
    /// Throws WalletNotFoundException, WalletInactiveException.
    /// </summary>
    Task<TransactionResponse> DepositAsync(
        string walletId,
        string userId,
        string idempotencyKey,
        DepositRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deactivates the wallet. Only permitted when balance is zero.
    /// Throws WalletBalanceNotZeroException, WalletAlreadyInactiveException.
    /// </summary>
    Task<WalletDeactivatedResponse> DeactivateWalletAsync(
        string walletId,
        string userId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Handles atomic peer-to-peer fund transfers between wallets.
/// Enforces idempotency, balance checks, and lock ordering to prevent deadlocks.
/// </summary>
public interface ITransferService
{
    /// <summary>
    /// Executes an atomic transfer from source to destination wallet.
    /// Lock ordering: always locks the wallet with the lexicographically lower ID first.
    /// Throws InsufficientFundsException, WalletNotFoundException, DuplicateTransferException.
    /// </summary>
    Task<TransferResponse> TransferAsync(
        string requestingUserId,
        string idempotencyKey,
        TransferRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provides read access to transaction history. Always reads from the read replica.
/// </summary>
public interface ITransactionService
{
    /// <summary>
    /// Returns a cursor-paginated list of transactions for the given wallet.
    /// Throws WalletNotFoundException, WalletAccessDeniedException.
    /// </summary>
    Task<PagedApiResponse<TransactionResponse>> GetTransactionsAsync(
        string walletId,
        string requestingUserId,
        GetTransactionsRequest query,
        CancellationToken cancellationToken);
}
