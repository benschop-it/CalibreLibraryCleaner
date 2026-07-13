# ADR 0002: Use Clean Architecture

- Status: Accepted
- Date: 2026-07-11

## Context

The application integrates with Calibre schema and CLI, filesystems, ebook parsers, optional AI, and WPF. Core duplicate and safety logic must remain independent and testable.

## Decision

Use Domain, Application, Infrastructure, and Wpf projects. Domain has no integration dependencies; Application owns use cases and ports; Infrastructure implements ports; WPF is presentation and composition.

## Consequences

This improves testability, replaceability, and safety boundaries at the cost of more projects and disciplined dependency management.

## Guardrails

Use architecture tests, keep attributes and integrations out of Domain, and create abstractions only at meaningful boundaries.
