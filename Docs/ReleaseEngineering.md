# Nerdbank.GitVersioning Release Guide (GitFlow & GitHub Actions)

This document describes how to manage automated software versioning using **Nerdbank.GitVersioning (NBGV)** in a GitFlow branching model, with continuous integration managed by **GitHub Actions**.

## 1. Prerequisites & Branching Strategy

Our development lifecycle follows a structured branching topology:
* `develop`: Main integration branch where feature branches are merged.
* `release/vX.Y`: Dedicated stabilization branch where release engineering takes place.
* `master`: Production-ready branch containing only stable deployments.

---

## 2. Configuration (`version.json`)

By default, NBGV appends a commit ID hash to any branch that is not designated as a public release. To ensure builds compiled on `master` or your `release/*` branches yield clean, stable version tags (e.g., `1.0.0` instead of `1.0.0+gabcdef`), you must explicitly configure the `publicReleaseRefSpec` rules.

Update the `version.json` file in your root directory:

```json
{
  "\$schema": "https://githubusercontent.com",
  "version": "1.0-beta",
  "publicReleaseRefSpec": [
    "^refs/heads/master\$",
    "^refs/heads/release/"
  ],
  "release": {
    "branchName": "release/v{version}"
  }
}
```

* **`^refs/heads/master$`**: Instructs NBGV that any execution on `master` generates a final production package.
* **`^refs/heads/release/`**: Ensures the release candidate branch strips pre-release modifiers (like `-beta`) and tracks stable numbers.

---

## 3. The Release Execution Loop

When a milestone is reached and a release cycle begins, use the following operational sequence:

1. **Synchronize Integration Context:**
   ```bash
   git checkout develop
   git pull origin develop
   ```

2. **Generate the Stabilization Branch:**
   Execute the preparation utility to freeze dependencies and update pointers:
   ```bash
   nbgv prepare-release
   ```
   * **What this does:** Clones the current status into a new branch (`release/v1.0`), strips the `-beta` tag on that branch, and increments `develop` to the next cycle baseline (e.g., `1.1-beta`).

3. **Publish to Remote Registry:**
   ```bash
   git push origin develop --tags
   git push origin release/v1.0
   ```

4. **Stabilization & Release Engineering:**
   Commit bugfixes and adjustments directly onto the `release/v1.0` branch. NBGV automatically tracks version revisions here without altering `develop`.

5. **Merge to Production:**
   When tests pass and quality assurance is complete, open a Pull Request from `release/v1.0` into `master`. 

6. **Downstream Synchronization:**
   Merge `master` back into `develop` to ensure hotfixes are preserved. 
   > ⚠️ **Note:** If a merge conflict arises in `version.json` during the back-merge, **keep the version file from `develop`**, as it contains the correct parameters for the upcoming development cycle.

---

## 4. CI/CD Pipeline Configuration

Save this workflow specification to your repository path under `.github/workflows/ci.yml`. It activates the NBGV compiler metadata step and runs conditional deployments for production branches.

```yaml
name: CI Build & Deploy

on:
  push:
    branches:
      - develop
      - master
      - 'release/*'

jobs:
  build:
    runs-on: ubuntu-latest

    steps:
    - name: Checkout Code
      uses: actions/checkout@v4
      with:
        fetch-depth: 0 # CRITICAL: NBGV relies on the full commit history to evaluate version height

    - name: Setup .NET Desktop/Core
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '8.0.x'

    - name: Activate Nerdbank.GitVersioning
      uses: dotnet/nbgv@v0.4.1
      id: nbgv # Instantiates environment arrays (e.g., \$NBGV_NuGetPackageVersion)

    - name: Restore Dependencies
      run: dotnet restore

    - name: Build Solution
      run: dotnet build --configuration Release --no-restore

    - name: Pack Artifacts
      run: dotnet pack --configuration Release --no-build

    # Conditional Execution: Triggers deployment pipelines strictly for production code signatures
    - name: Publish Production Release
      if: github.ref == 'refs/heads/master'
      run: |
        echo "Deploying version \${{ steps.nbgv.outputs.SimpleVersion }} to Production Env!"
        # Insert target dotnet nuget push or server staging scripts here
```

