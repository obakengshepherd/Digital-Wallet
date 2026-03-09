# Digital Wallet Backend System

## Overview

This project implements a scalable **digital wallet backend** similar to systems used in modern fintech platforms.

The system enables users to store balances, transfer funds between accounts, and maintain a reliable financial ledger while ensuring transactional consistency.

This project focuses on **financial system reliability, consistency guarantees, and scalable architecture design**.

---

## Repository Structure

digital-wallet-system
├── docs/
│ ├── architecture.md
│ ├── api-spec.md
│ └── scaling.md
│
├── src/
│ ├── Api/
│ ├── Application/
│ ├── Domain/
│ ├── Infrastructure/
│
├── tests/
├── docker/
├── scripts/
├── docker-compose.yml
└── README.md

---

## Key Capabilities

- Wallet account creation
- Secure balance management
- Peer-to-peer money transfers
- Transaction ledger history
- Idempotent transaction processing
- API authentication
- Rate limiting

---

## System Architecture

Client  
↓  
API Gateway  
↓  
Wallet Service  
↓  
Event Queue  
↓  
PostgreSQL Ledger

---

## Core Technologies

- .NET Web API
- PostgreSQL
- Redis
- Kafka / RabbitMQ
- Docker

---

## Key APIs

POST /wallets  
POST /wallets/deposit  
POST /wallets/transfer  
GET /wallets/{id}  
GET /transactions/{walletId}

---

## Design Considerations

Financial systems require:

- strict transaction consistency
- idempotency protection
- reliable transaction logs

Balance updates use **ACID transactions** to maintain ledger integrity.

---

## Scaling Strategy

- Redis caching for balance lookups
- Message queues for transaction processing
- Database read replicas
- Horizontal API scaling behind load balancers

---

## Future Enhancements

- Fraud detection integration
- multi-currency wallets
- external payment gateway integration
- ledger reconciliation
