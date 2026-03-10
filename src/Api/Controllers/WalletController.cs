using DigitalWallet.Application.Interfaces;
using DigitalWallet.Api.Models.Requests;
using DigitalWallet.Api.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalWallet.Api.Controllers;

[ApiController]
[Route("api/v1/wallets")]
[Authorize]
public class WalletController : ControllerBase
{
    private readonly IWalletService _walletService;
    private readonly ITransferService _transferService;
    private readonly ITransactionService _transactionService;

    public WalletController(
        IWalletService walletService,
        ITransferService transferService,
        ITransactionService transactionService)
    {
        _walletService = walletService;
        _transferService = transferService;
        _transactionService = transactionService;
    }

    // POST /api/v1/wallets
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<WalletResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateWallet(
        [FromBody] CreateWalletRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var result = await _walletService.CreateWalletAsync(userId, request, cancellationToken);
        return CreatedAtAction(nameof(GetWallet), new { id = result.Id }, ApiResponse.Success(result));
    }

    // POST /api/v1/wallets/{id}/deposit
    [HttpPost("{id}/deposit")]
    [ProducesResponseType(typeof(ApiResponse<TransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Deposit(
        [FromRoute] string id,
        [FromBody] DepositRequest request,
        [FromHeader(Name = "X-Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var result = await _walletService.DepositAsync(id, userId, idempotencyKey, request, cancellationToken);
        return Ok(ApiResponse.Success(result));
    }

    // POST /api/v1/wallets/transfer
    [HttpPost("transfer")]
    [ProducesResponseType(typeof(ApiResponse<TransferResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Transfer(
        [FromBody] TransferRequest request,
        [FromHeader(Name = "X-Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var result = await _transferService.TransferAsync(userId, idempotencyKey, request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse.Success(result));
    }

    // GET /api/v1/wallets/{id}
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApiResponse<WalletResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWallet(
        [FromRoute] string id,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var result = await _walletService.GetWalletAsync(id, userId, cancellationToken);
        return Ok(ApiResponse.Success(result));
    }

    // GET /api/v1/wallets/{id}/transactions
    [HttpGet("{id}/transactions")]
    [ProducesResponseType(typeof(PagedApiResponse<TransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransactions(
        [FromRoute] string id,
        [FromQuery] GetTransactionsRequest query,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var result = await _transactionService.GetTransactionsAsync(id, userId, query, cancellationToken);
        return Ok(result);
    }

    // DELETE /api/v1/wallets/{id}
    [HttpDelete("{id}")]
    [ProducesResponseType(typeof(ApiResponse<WalletDeactivatedResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> DeactivateWallet(
        [FromRoute] string id,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var result = await _walletService.DeactivateWalletAsync(id, userId, cancellationToken);
        return Ok(ApiResponse.Success(result));
    }
}
