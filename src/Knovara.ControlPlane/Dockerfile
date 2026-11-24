FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Knovara.ControlPlane/Knovara.ControlPlane.csproj src/Knovara.ControlPlane/
RUN dotnet restore src/Knovara.ControlPlane/Knovara.ControlPlane.csproj

COPY src/Knovara.ControlPlane/ src/Knovara.ControlPlane/
RUN dotnet publish src/Knovara.ControlPlane/Knovara.ControlPlane.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Uploaded installers live here; mount a volume so they survive container replacement.
ENV Releases__Directory=/data/releases
VOLUME /data

EXPOSE 8080
ENTRYPOINT ["dotnet", "Knovara.ControlPlane.dll"]
