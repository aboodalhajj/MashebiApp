# ===== Build =====
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# نسخ الـ csproj فقط أولاً (للاستفادة من طبقات الكاش)
COPY MashebiApi/MashebiApi.csproj MashebiApi/
RUN dotnet restore MashebiApi/MashebiApi.csproj

# نسخ بقية السورس
COPY . .

# نشر المشروع (مش السولوشن)
RUN dotnet publish MashebiApi/MashebiApi.csproj -c Release -o /app /p:UseAppHost=false

# ===== Run =====
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# شهادات الجذور (TLS)
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","MashebiApi.dll"]
