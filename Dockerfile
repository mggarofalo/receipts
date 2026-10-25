# Build only the privilege-drop helper with a supported, pinned Go toolchain.
# Ubuntu's gosu package still embeds Go 1.22.2. Keep the application's base unchanged.
FROM --platform=$BUILDPLATFORM golang:1.27.2-alpine@sha256:f92b6ef800e499660581efdabdf25d9d817a9d124eaf900924f0504e7e27e12d AS gosu-build
ARG TARGETARCH
WORKDIR /src/gosu
ADD --checksum=sha256:33d7537d588ea49458b9509bcf4554bdf5ceacc66da71e5caa1058ea3b689c3b https://codeload.github.com/tianon/gosu/tar.gz/6456aaa0f3c854d199d0f037f068eb97515b7513 /tmp/gosu.tar.gz
# Pin security fixes beyond upstream 1.19's module versions. Checksums come from
# sum.golang.org; readonly build verifies content and prevents implicit upgrades.
RUN tar -xzf /tmp/gosu.tar.gz --strip-components=1 && \
    go mod edit -go=1.25.0 -require=github.com/moby/sys/user@v0.4.1 -require=golang.org/x/sys@v0.44.0 && \
    printf '%s\n' \
      'github.com/moby/sys/user v0.4.1 h1:RgjRlaDKi/Xmyrz4t8lyzXT6v2ooFeO/7xtchmhVWE0=' \
      'github.com/moby/sys/user v0.4.1/go.mod h1:E9QsW5WRe1kUAf7kW8hXKwu1uhsZEAdPLYHYSDudF4Y=' \
      'golang.org/x/sys v0.44.0 h1:ildZl3J4uzeKP07r2F++Op7E9B29JRUy+a27EibtBTQ=' \
      'golang.org/x/sys v0.44.0/go.mod h1:4GL1E5IUh+htKOUEOaiffhrAeqysfVGipDYzABqnCmw=' >> go.sum && \
    CGO_ENABLED=0 GOOS=linux GOARCH=${TARGETARCH} go build -mod=readonly -trimpath -buildvcs=false -o /out/gosu .

# Stage 1: Build React SPA
FROM node:24.21.0-alpine AS client-build
WORKDIR /app/client

# Copy only files needed for npm install first (layer caching)
COPY src/client/package.json src/client/package-lock.json ./

RUN npm ci

# Copy OpenAPI spec (needed for type generation) and client source
COPY openapi/spec.yaml /openapi/spec.yaml
# The client typecheck includes tests sharing the backend arithmetic contract.
COPY test-data/receipt-arithmetic.json /test-data/receipt-arithmetic.json
COPY src/client/ ./

# Inject version info and Sentry config after npm ci so the install layer stays cached
ARG VITE_APP_VERSION=dev
ARG VITE_COMMIT_HASH=local
ARG VITE_SENTRY_DSN=
ARG VITE_SENTRY_ENVIRONMENT=production
ARG SENTRY_AUTH_TOKEN=
ARG SENTRY_ORG=
ARG SENTRY_PROJECT=
ENV VITE_APP_VERSION=${VITE_APP_VERSION}
ENV VITE_COMMIT_HASH=${VITE_COMMIT_HASH}
ENV VITE_SENTRY_DSN=${VITE_SENTRY_DSN}
ENV VITE_SENTRY_ENVIRONMENT=${VITE_SENTRY_ENVIRONMENT}

RUN npm run build

# Tool-free ChatGPT provider. Use a glibc Node binary for the Ubuntu runtime.
FROM node:24.21.0-bookworm-slim AS chatgpt-runner
WORKDIR /runner
COPY runner/package.json runner/package-lock.json runner/repair-shrinkwrap.mjs ./
RUN npm ci --ignore-scripts --omit=dev --no-audit --no-fund && node repair-shrinkwrap.mjs
COPY runner/bridge.mjs runner/protocol.mjs runner/errors.mjs ./

# Stage 2: Build .NET API and CLI tools
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src

ARG TARGETARCH

# Explicit version for the published assemblies. The Docker build context does not
# include the `.git` directory, so MinVer cannot compute the version inside the
# image build — the CI workflow derives it from the release tag and passes it here.
# Defaults to 0.0.0-dev for local/non-tagged image builds.
#
# This value is fed to MinVer as MinVerVersionOverride (NOT the bare `Version`
# property): MinVer's build target unconditionally sets `Version` from its own
# computed value, so a plain `-p:Version=` would be overridden. MinVerVersionOverride
# makes MinVer itself emit this version and skip the git lookup entirely.
ARG VERSION=0.0.0-dev

# Copy project files needed for restore (layer caching)
COPY Directory.Packages.props Directory.Build.props* ./
COPY src/Domain/Domain.csproj src/Domain/
COPY src/Common/Common.csproj src/Common/
COPY src/Application/Application.csproj src/Application/
COPY src/Infrastructure/Infrastructure.csproj src/Infrastructure/
COPY src/Presentation/API/API.csproj src/Presentation/API/
COPY src/Receipts.ServiceDefaults/Receipts.ServiceDefaults.csproj src/Receipts.ServiceDefaults/
COPY src/Tools/DbMigrator/DbMigrator.csproj src/Tools/DbMigrator/
COPY src/Tools/DbSeeder/DbSeeder.csproj src/Tools/DbSeeder/

# Copy NuGet config and OpenAPI tooling (needed for restore/build)
COPY nuget.config* ./
COPY nswag.json ./
COPY openapi/ openapi/
COPY tools/ tools/

RUN case ${TARGETARCH} in \
      amd64) DOTNET_ARCH=x64 ;; \
      arm64) DOTNET_ARCH=arm64 ;; \
      arm)   DOTNET_ARCH=arm ;; \
      *) echo "Unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac && \
    dotnet restore src/Presentation/API/API.csproj -r linux-${DOTNET_ARCH} -p:PublishReadyToRun=true && \
    dotnet restore src/Tools/DbMigrator/DbMigrator.csproj -r linux-${DOTNET_ARCH} && \
    dotnet restore src/Tools/DbSeeder/DbSeeder.csproj -r linux-${DOTNET_ARCH} && \
    dotnet restore tools/Receipts.TaxonomyMigration/Receipts.TaxonomyMigration.csproj -r linux-${DOTNET_ARCH}

# Copy remaining source
COPY src/ src/
COPY docs/normalization-evaluation/corpus.v1.json docs/normalization-evaluation/corpus.v1.json
COPY docs/normalization-evaluation/catalog.v2.json docs/normalization-evaluation/catalog.v2.json

# The ONNX embedding model (~1.34 GB) is NOT downloaded here. Baking it in made the image
# 2.54 GB — three copies of one file, since MSBuild flowed it into the CLI tools' output too
# — and re-pushed an immutable blob on every release. The app now fetches it once into
# /data on first start; see EmbeddingModelProvisioningService (RECEIPTS-929).

RUN case ${TARGETARCH} in \
      amd64) DOTNET_ARCH=x64 ;; \
      arm64) DOTNET_ARCH=arm64 ;; \
      arm)   DOTNET_ARCH=arm ;; \
      *) echo "Unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/Presentation/API/API.csproj \
      -c Release -o /app/publish --no-restore \
      -p:PublishReadyToRun=true -p:OpenApiGenerateDocumentsOnBuild=false \
      -p:MinVerVersionOverride=${VERSION} \
      -r linux-${DOTNET_ARCH} && \
    dotnet publish src/Tools/DbMigrator/DbMigrator.csproj \
      -c Release -o /app/tools/DbMigrator --no-restore \
      -p:MinVerVersionOverride=${VERSION} \
      -r linux-${DOTNET_ARCH} && \
    dotnet publish src/Tools/DbSeeder/DbSeeder.csproj \
      -c Release -o /app/tools/DbSeeder --no-restore \
      -p:MinVerVersionOverride=${VERSION} \
      -r linux-${DOTNET_ARCH} && \
    dotnet publish tools/Receipts.TaxonomyMigration/Receipts.TaxonomyMigration.csproj \
      -c Release -o /app/tools/TaxonomyMigration --no-restore \
      -p:MinVerVersionOverride=${VERSION} \
      -r linux-${DOTNET_ARCH}

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

LABEL org.opencontainers.image.title="Receipts" \
      org.opencontainers.image.description="Receipt management API with React SPA" \
      org.opencontainers.image.source="https://github.com/mggarofalo/Receipts"

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl \
      libgomp1 && \
    rm -rf /var/lib/apt/lists/*

ARG SENTRY_BACKEND_DSN=
ENV SENTRY_BACKEND_DSN=${SENTRY_BACKEND_DSN}
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080

# Model cache on the mounted /data volume, so it survives image upgrades and is downloaded
# once per deployment rather than shipped in every image tag.
ENV Embeddings__ModelPath=/data/models/BgeLargeEnV15

WORKDIR /app

COPY --from=gosu-build /out/gosu /usr/local/bin/gosu
# Retain redistribution notices for gosu and its statically linked dependencies.
COPY --from=gosu-build /src/gosu/LICENSE /usr/share/licenses/gosu/LICENSE
COPY --from=gosu-build /go/pkg/mod/github.com/moby/sys/user@v0.4.1/LICENSE /usr/share/licenses/gosu/moby-sys-user-LICENSE
COPY --from=gosu-build /go/pkg/mod/golang.org/x/sys@v0.44.0/LICENSE /usr/share/licenses/gosu/golang-x-sys-LICENSE
COPY --from=gosu-build /usr/local/go/LICENSE /usr/share/licenses/gosu/go-LICENSE

# Copy published API
COPY --from=api-build /app/publish .

# Copy CLI tools
COPY --from=api-build /app/tools ./tools/

# The provider SDK owns OAuth and refresh in its separate persistent volume.
COPY --from=chatgpt-runner /usr/local/bin/node /usr/local/bin/node
COPY --from=chatgpt-runner /runner ./runner/

# Copy React SPA into wwwroot
COPY --from=client-build /app/client/dist ./wwwroot/

# Copy entrypoint script
COPY docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh

# Create mount points
RUN mkdir -p /secrets /data

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=120s --retries=5 \
    CMD curl -sf http://localhost:8080/api/health || exit 1

ENTRYPOINT ["./docker-entrypoint.sh"]
