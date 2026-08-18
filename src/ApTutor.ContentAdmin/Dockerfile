# Tutor AI Content Admin — hosted SME review app.
#
# S3 is the source of truth for approved content now (git is application source code only), which
# makes this app genuinely stateless — no persistent volume needed. Whoever deploys this must
# provide: (1) an IAM role/credentials with the policy in the plan, scoped to read/write on the
# content bucket, (2) the environment variables this app reads at runtime: ANTHROPIC_API_KEY,
# ANTHROPIC_MODEL, CONTENT_ADMIN_PASSWORD_HASH (see Services/PasswordHasher.cs to generate one),
# ContentAdmin__AdminUsername, ContentStore__Bucket, ContentStore__Region. AWS credentials
# themselves come from the SDK's normal resolution chain (env vars locally, an attached instance
# role on App Runner/Fargate/EC2) — nothing AWS-credential-specific needs to be passed explicitly.
#
# No persistent auth cookie key ring either — a redeploy logs out every SME session. Accepted
# tradeoff for a low-traffic internal tool (see the plan); revisit if this ever needs to survive
# redeploys without logging everyone out, or if it's ever run as more than one instance at a time
# (both the auth cookie and the in-memory unapproved-draft tracking assume a single instance).

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

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "ApTutor.ContentAdmin.dll"]
