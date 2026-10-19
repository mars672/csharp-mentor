# Этап 1: сборка
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копируем .csproj и восстанавливаем зависимости (кэшируется)
COPY CSharpMentor/CSharpMentor.csproj ./CSharpMentor/
RUN dotnet restore CSharpMentor/CSharpMentor.csproj

# Копируем весь исходный код и собираем
COPY CSharpMentor/ ./CSharpMentor/
RUN dotnet publish CSharpMentor/CSharpMentor.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Этап 2: финальный образ (только runtime, без SDK — меньше размер)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Копируем собранное приложение
COPY --from=build /app/publish .

EXPOSE 8080

# Railway передаёт порт через переменную PORT (читается при запуске контейнера)
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet CSharpMentor.dll"]
