# API Specification — Digital Wallet System

---

## Overview

The Digital Wallet API provides programmatic access to wallet management, fund transfers, and
transaction history. It is consumed by mobile clients, web frontends, and internal services
(such as the Fraud Detection System). All financial operations are idempotency-safe and ACID-
backed. The API enforces strict input validation at the boundary; no business logic leaks into
the transport layer.

---

## Base URL and Versioning

```
https://api.wallet.internal/api/v1
```

Versioning is path-based. Breaking changes (field removal, semantic changes to existing
endpoints) increment the version to `/api/v2`. Additive changes (new optional fields, new
endpoints) do not require a version increment. The `v1` prefix is always required.

---

## Authentication

All endpoints require a Bearer token in the `Authorization` header:

```
Authorization: Bearer <jwt_token>
```

Tokens are issued by the upstream Identity Service (out of scope for this system). The
middleware validates the token signature, expiry, and extracts `user_id` and `role` claims.
Requests with missing or invalid tokens receive **401 Unauthorized**.

---

## Common Response Envelope

### Success

```json
{
  "data": { ... },
  "meta": {
    "request_id": "uuid",
    "timestamp": "2024-01-15T10:30:00Z"
  }
}
```

### Error

```json
{
  "error": {
    "code": "INSUFFICIENT_FUNDS",
    "message": "Source wallet does not have sufficient balance.",
    "details": [
      { "field": "amount", "issue": "Requested 500.00, available 200.00" }
    ]
  },
  "meta": {
    "request_id": "uuid",
    "timestamp": "2024-01-15T10:30:00Z"
  }
}
```

### Pagination (cursor-based)

```json
{
  "data": [ ... ],
  "pagination": {
    "cursor": "eyJpZCI6MTIzfQ==",
    "has_more": true,
    "limit": 20
  },
  "meta": { ... }
}
```

---

## Rate Limiting

| Tier            | Limit                   | Scope          |
|-----------------|-------------------------|----------------|
| Authenticated   | 100 requests / minute   | Per user       |
| Transfer writes | 10 requests / minute    | Per wallet     |
| Deposit writes  | 20 requests / minute    | Per wallet     |

**Headers returned on every response:**

```
X-RateLimit-Limit: 100
X-RateLimit-Remaining: 87
X-RateLimit-Reset: 1705312260
```

When the limit is exceeded, the API returns **429 Too Many Requests** with:

```
Retry-After: 37
```

---

## Idempotency

Write endpoints (`POST /wallets`, `POST /wallets/transfer`, `POST /wallets/{id}/deposit`)
require an idempotency key:

```
X-Idempotency-Key: <uuid-v4>
```

If a request with the same key is received within 24 hours, the cached response is returned
without re-executing the operation. The key must be a valid UUID v4.

---

## Endpoints

---

### POST /wallets

**Description:** Creates a new wallet for the authenticated user in the specified currency.
One wallet per user per currency is enforced.

**Request Body:**

| Field      | Type   | Required | Validation              | Example  |
|------------|--------|----------|-------------------------|----------|
| `currency` | string | Yes      | ISO 4217, 3 chars upper | `"GBP"`  |

**Example Request:**
```json
{
  "currency": "GBP"
}
```

**Response — 201 Created:**
```json
{
  "data": {
    "id": "wlt_01j9z3k4m5n6p7q8r9s0",
    "user_id": "usr_abc123",
    "currency": "GBP",
    "balance": "0.00",
    "status": "ACTIVE",
    "created_at": "2024-01-15T10:30:00Z"
  },
  "meta": { "request_id": "req_xyz", "timestamp": "2024-01-15T10:30:00Z" }
}
```

**Status Codes:**

| Code | Condition                                              |
|------|--------------------------------------------------------|
| 201  | Wallet created successfully                            |
| 400  | Invalid currency code or missing required field        |
| 401  | Missing or invalid Bearer token                        |
| 409  | User already has a wallet in this currency             |
| 422  | Currency not supported by the system                   |
| 500  | Internal server error                                  |

---

### POST /wallets/{id}/deposit

**Description:** Credits the specified wallet with the given amount. Requires idempotency key.

**Path Parameters:**

| Parameter | Type   | Description   |
|-----------|--------|---------------|
| `id`      | string | Wallet ID     |

**Request Headers:** `X-Idempotency-Key: <uuid-v4>` *(required)*

**Request Body:**

| Field       | Type    | Required | Validation        | Example      |
|-------------|---------|----------|-------------------|--------------|
| `amount`    | decimal | Yes      | > 0, max 2 dp     | `"250.00"`   |
| `reference` | string  | No       | max 128 chars     | `"Top-up"`   |

**Example Request:**
```json
{
  "amount": "250.00",
  "reference": "Monthly top-up"
}
```

**Response — 200 OK:**
```json
{
  "data": {
    "transaction_id": "txn_01j9z3k4m5n6p7q8",
    "wallet_id": "wlt_01j9z3k4m5n6p7q8r9s0",
    "type": "CREDIT",
    "amount": "250.00",
    "balance_after": "250.00",
    "status": "COMPLETED",
    "created_at": "2024-01-15T10:31:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                     |
|------|-----------------------------------------------|
| 200  | Deposit completed                             |
| 400  | Invalid amount or missing field               |
| 401  | Unauthorized                                  |
| 404  | Wallet not found                              |
| 409  | Duplicate idempotency key (returns original)  |
| 422  | Wallet is not ACTIVE                          |
| 429  | Rate limit exceeded                           |
| 500  | Internal server error                         |

---

### POST /wallets/transfer

**Description:** Atomically transfers funds from the authenticated user's wallet to a
destination wallet. Both wallets must be ACTIVE. Requires idempotency key.

**Request Headers:** `X-Idempotency-Key: <uuid-v4>` *(required)*

**Request Body:**

| Field                 | Type    | Required | Validation                        | Example                        |
|-----------------------|---------|----------|-----------------------------------|--------------------------------|
| `source_wallet_id`    | string  | Yes      | Must belong to authenticated user | `"wlt_01j9z3k4m5n6p7q8r9s0"`  |
| `destination_wallet_id` | string | Yes     | Must differ from source           | `"wlt_99z9z9k4m5n6p7q8r9s1"`  |
| `amount`              | decimal | Yes      | > 0, <= source balance, max 2 dp  | `"100.00"`                     |
| `note`                | string  | No       | max 256 chars                     | `"Dinner split"`               |

**Example Request:**
```json
{
  "source_wallet_id": "wlt_01j9z3k4m5n6p7q8r9s0",
  "destination_wallet_id": "wlt_99z9z9k4m5n6p7q8r9s1",
  "amount": "100.00",
  "note": "Dinner split"
}
```

**Response — 201 Created:**
```json
{
  "data": {
    "transfer_id": "trf_01j9z3k4m5n6p7q8",
    "source_wallet_id": "wlt_01j9z3k4m5n6p7q8r9s0",
    "destination_wallet_id": "wlt_99z9z9k4m5n6p7q8r9s1",
    "amount": "100.00",
    "status": "COMPLETED",
    "source_balance_after": "150.00",
    "created_at": "2024-01-15T10:32:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                               |
|------|---------------------------------------------------------|
| 201  | Transfer completed                                      |
| 400  | Missing/invalid fields, source == destination           |
| 401  | Unauthorized                                            |
| 404  | Source or destination wallet not found                  |
| 409  | Duplicate idempotency key (returns original result)     |
| 422  | Insufficient funds, wallet not ACTIVE, currency mismatch|
| 429  | Rate limit exceeded (10/min per wallet)                 |
| 500  | Internal server error                                   |

---

### GET /wallets/{id}

**Description:** Returns the current balance and status of a wallet owned by the authenticated user.

**Path Parameters:**

| Parameter | Type   | Description |
|-----------|--------|-------------|
| `id`      | string | Wallet ID   |

**Response — 200 OK:**
```json
{
  "data": {
    "id": "wlt_01j9z3k4m5n6p7q8r9s0",
    "user_id": "usr_abc123",
    "currency": "GBP",
    "balance": "150.00",
    "status": "ACTIVE",
    "created_at": "2024-01-15T10:30:00Z",
    "updated_at": "2024-01-15T10:32:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                 |
|------|-------------------------------------------|
| 200  | Success                                   |
| 401  | Unauthorized                              |
| 403  | Wallet belongs to a different user        |
| 404  | Wallet not found                          |

---

### GET /wallets/{id}/transactions

**Description:** Returns a paginated, cursor-based list of transactions for the given wallet,
ordered by `created_at` descending.

**Path Parameters:**

| Parameter | Type   | Description |
|-----------|--------|-------------|
| `id`      | string | Wallet ID   |

**Query Parameters:**

| Parameter | Type    | Default | Description                         |
|-----------|---------|---------|-------------------------------------|
| `limit`   | integer | `20`    | Page size, max 100                  |
| `cursor`  | string  | —       | Opaque pagination cursor            |
| `type`    | string  | —       | Filter: `CREDIT`, `DEBIT`           |

**Response — 200 OK:**
```json
{
  "data": [
    {
      "id": "txn_01j9z3k4m5n6p7q8",
      "type": "DEBIT",
      "amount": "100.00",
      "balance_after": "150.00",
      "reference_id": "trf_01j9z3k4m5n6p7q8",
      "note": "Dinner split",
      "created_at": "2024-01-15T10:32:00Z"
    }
  ],
  "pagination": {
    "cursor": "eyJpZCI6InR4bl8wMSJ9",
    "has_more": true,
    "limit": 20
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                          |
|------|------------------------------------|
| 200  | Success                            |
| 400  | Invalid query parameters           |
| 401  | Unauthorized                       |
| 403  | Wallet belongs to a different user |
| 404  | Wallet not found                   |

---

### DELETE /wallets/{id}

**Description:** Deactivates a wallet. The wallet must have a zero balance. Deactivated
wallets cannot receive or send funds but retain their transaction history.

**Path Parameters:**

| Parameter | Type   | Description |
|-----------|--------|-------------|
| `id`      | string | Wallet ID   |

**Response — 200 OK:**
```json
{
  "data": {
    "id": "wlt_01j9z3k4m5n6p7q8r9s0",
    "status": "INACTIVE",
    "deactivated_at": "2024-01-15T12:00:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                 |
|------|-------------------------------------------|
| 200  | Wallet deactivated                        |
| 401  | Unauthorized                              |
| 403  | Wallet belongs to a different user        |
| 404  | Wallet not found                          |
| 422  | Wallet balance is not zero                |
| 422  | Wallet is already INACTIVE                |
