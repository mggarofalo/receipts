# Stage 1: Build React SPA
FROM node:22-alpine AS client-build
WORKDIR /app/client

# Copy only files needed for npm install first (layer caching)
COPY src/client/package.json src/client/package-lock.json ./

RUN npm ci

# Copy OpenAPI spec (needed for type generation) and client source
COPY openapi/spec.yaml /openapi/spec.yaml
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
    dotnet restore src/Tools/DbSeeder/DbSeeder.csproj -r linux-${DOTNET_ARCH}

# Copy remaining source
COPY src/ src/

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
      -r linux-${DOTNET_ARCH}

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

LABEL org.opencontainers.image.title="Receipts" \
      org.opencontainers.image.description="Receipt management API with React SPA" \
      org.opencontainers.image.source="https://github.com/mggarofalo/Receipts"

RUN apt-get update && \
    apt-get install -y --no-install-recommends gosu curl \
      libgomp1 libgdiplus && \
    rm -rf /var/lib/apt/lists/*

ARG SENTRY_BACKEND_DSN=
ENV SENTRY_BACKEND_DSN=${SENTRY_BACKEND_DSN}
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080

# Model cache on the mounted /data volume, so it survives image upgrades and is downloaded
# once per deployment rather than shipped in every image tag.
ENV Embeddings__ModelPath=/data/models/BgeLargeEnV15

WORKDIR /app

# Copy published API
COPY --from=api-build /app/publish .

# Copy CLI tools
COPY --from=api-build /app/tools ./tools/

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
