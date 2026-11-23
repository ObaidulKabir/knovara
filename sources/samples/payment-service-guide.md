# Payment Service Operations Guide

This guide describes how the payment service handles failed charges, retries and refunds.

## Failure Handling

When a charge fails, the payment service records the failure in PaymentStatus and decides whether the failure is retryable. Card declines are final; network timeouts and gateway 5xx errors are retryable.

### Retry Policy

Retry configuration is maintained in RetryPolicy. The service retries a retryable failure up to five times with exponential backoff, starting at two seconds and doubling each attempt. The maximum retry interval is capped at sixty seconds.

| Setting | Default | Where it is configured |
|---|---|---|
| Max attempts | 5 | RetryPolicy.MaxAttempts |
| Initial delay | 2 s | RetryPolicy.InitialDelay |
| Max interval | 60 s | RetryPolicy.MaxInterval |

### Idempotency

Every charge request carries an idempotency key. If a retry reaches the gateway after the original request already succeeded, the gateway returns the original result instead of charging twice.

## Refunds

Refunds are processed asynchronously. A refund request is accepted immediately and settled within five business days. Partial refunds are allowed as long as the total refunded never exceeds the original charge.
