# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["HiileikeHome.csproj", "./"]
RUN dotnet restore "HiileikeHome.csproj"
COPY . .
RUN dotnet build "HiileikeHome.csproj" -c Release -o /app/build
RUN dotnet publish "HiileikeHome.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Serve Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "HiileikeHome.dll"]