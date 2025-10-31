# ===== Build =====
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

# ===== Run =====
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .

# Railway يمرر PORT، ونحتاج أن نستمع على 0.0.0.0
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

# عدّل الاسم لو كان مشروعك باسم مختلف
ENTRYPOINT ["dotnet","MashebiApi.dll"]

