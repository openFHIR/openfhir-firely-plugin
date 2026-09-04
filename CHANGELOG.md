# Changelog

All notable changes to the openFHIR Firely Server plugin are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Release notes are generated from this file by the [release workflow](.github/workflows/release.yml):
the section matching the `<Version>` in `OpenFhirFirelyPlugin.csproj` becomes the body of the GitHub
release, together with the Firely Server compatibility derived from the referenced Vonk SDK version.

## [Unreleased]

## [2.0.0] - 2026-09-04

Requires **openFHIR 3.0.0 or later** — the plugin now talks to openFHIR through its FHIR-native
operations instead of the legacy REST endpoints.

### Changed

- **Breaking:** FHIR → openEHR conversion now uses `POST /$toopenehr?format=canonical`
  (Bundle body, `application/fhir+json`) instead of the legacy convert endpoint.
- **Breaking:** openEHR → FHIR conversion now uses `POST /$tofhir` with a `Parameters`
  request (`composition`, `templateId`, and `context` with `ehr_id`/`patient`).
- The new operations are addressed at the openFHIR server root (no `/openfhir` prefix);
  errors are returned and parsed as `OperationOutcome`.
- AQL translation still uses the legacy `/openfhir/toaql` endpoint, which has no
  3.0.0 replacement.

### Fixed

- Engine-generated `OperationOutcome` entries and engine-marked `Provenance` entries are
  stripped from `$tofhir` response bundles (with the bundle `total` adjusted);
  mapping-produced `Provenance` resources are preserved.

### Added

- xUnit unit test suite for `OpenFhirClient` (stubbed HTTP, no running server required).
- GitHub Actions CI (build + unit tests) and release workflow; this changelog.
- E2E test hardening: containers are cleaned up after each run, and the Firely Server
  `appsettings.json` used by the E2E stack is tracked in the repository.

## [1.1.0] - 2026-05-26

### Added

- FHIR STU3 support — the plugin now handles STU3, R4, and R5 information models,
  dispatching serialization and Vonk integration per request version.
- Additional IPS sections in the `$summary` (International Patient Summary) operation,
  including `DeviceUseStatement` in the IPS query FHIRPaths.

### Changed

- `toaql` transaction handling amended for newer openFHIR functionality.

### Fixed

- Hardcoded `templateId` in the IPS summary flow — the template is now resolved
  from configuration.

## [1.0.0] - 2026-05-11

Initial release of the openFHIR Firely Server plugin — a port of the Java
[openfhir-hapi-interceptor](https://github.com/openFHIR/openfhir-hapi-interceptor) to a
Firely Server (.NET 8) plugin.

### Added

- Transparent routing of FHIR create/update/delete requests for mapped resource types to an
  openEHR CDR through openFHIR, while unmapped requests pass through to Firely Server.
- FHIR search over openEHR data via openFHIR AQL translation (`toaql`).
- Automatic EHR provisioning in the openEHR CDR when a `Patient` is created, with a
  compensating delete if provisioning fails.
- `$summary` operation producing an International Patient Summary from openEHR data.
- Multi-CDR configuration via `cdrs.yml`.

[Unreleased]: https://github.com/openFHIR/openfhir-firely-plugin/compare/2.0.0...HEAD
[2.0.0]: https://github.com/openFHIR/openfhir-firely-plugin/compare/1.1.0...2.0.0
[1.1.0]: https://github.com/openFHIR/openfhir-firely-plugin/compare/1.0.0...1.1.0
[1.0.0]: https://github.com/openFHIR/openfhir-firely-plugin/releases/tag/1.0.0
