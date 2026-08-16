# Tutor AI Content Admin — hosted SME review app.
#
# The eventual host is not chosen here (no cloud account access — see the plan's "Hosting target"
# note); this just makes the app portable to whatever gets picked. Two things whoever deploys this
# must provide: (1) a persistent volume mounted at /data (the git clone + ASP.NET Data Protection
# key ring both need to survive restarts — otherwise a routine redeploy silently drops in-progress
# reviews and force-logs-out every session), and (2) the environment variables this app reads at
# runtime: ANTHROPIC_API_KEY, ANTHROPIC_MODEL, CONTENT_ADMIN_REPO_URL, CONTENT_ADMIN_REPO_PAT,
# CONTENT_ADMIN_PASSWORD_HASH (see Services/PasswordHasher.cs to generate one), and optionally
# CONTENT_ADMIN_COMMIT_NAME / CONTENT_ADMIN_COMMIT_EMAIL.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY AdvancedTestPrepper.sln .
COPY src/ApTutor.ContentAdmin/ApTutor.ContentAdmin.csproj src/ApTutor.ContentAdmin/
COPY src/ApTutor.Curriculum/ApTutor.Curriculum.csproj src/ApTutor.Curriculum/
COPY src/ApTutor.Platform/ApTutor.Platform.csproj src/ApTutor.Platform/
COPY src/ApTutor.Scene/ApTutor.Scene.csproj src/ApTutor.Scene/
COPY src/ApTutor.Content/ApTutor.Content.csproj src/ApTutor.Content/
COPY src/ApTutor.ContentFactory/ApTutor.ContentFactory.csproj src/ApTutor.ContentFactory/
RUN dotnet restore src/ApTutor.ContentAdmin/ApTutor.ContentAdmin.csproj

COPY src/ApTutor.ContentAdmin/ src/ApTutor.ContentAdmin/
COPY src/ApTutor.Curriculum/ src/ApTutor.Curriculum/
COPY src/ApTutor.Platform/ src/ApTutor.Platform/
COPY src/ApTutor.Scene/ src/ApTutor.Scene/
COPY src/ApTutor.Content/ src/ApTutor.Content/
COPY src/ApTutor.ContentFactory/ src/ApTutor.ContentFactory/

RUN dotnet publish src/ApTutor.ContentAdmin/ApTutor.ContentAdmin.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# git is not present in the base aspnet image — GitRepoService shells out to the real `git` binary.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
ENV ContentAdmin__RepoCloneDir=/data/repo
VOLUME ["/data"]
EXPOSE 8080

ENTRYPOINT ["dotnet", "ApTutor.ContentAdmin.dll"]
