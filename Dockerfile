# Etapa de ejecución (Base)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# Railway usa la variable PORT, pero ASP.NET 10 escucha por defecto en 8080 si no se indica lo contrario
EXPOSE 8080

# Etapa de construcción (Build)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Copia el archivo de proyecto y restaura dependencias
COPY ["AppN8N.csproj", "./"]
RUN dotnet restore "AppN8N.csproj"

# Copia el resto del código y compila
COPY . .
RUN dotnet build "AppN8N.csproj" -c Release -o /app/build

# Etapa de publicación (Publish)
FROM build AS publish
RUN dotnet publish "AppN8N.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Etapa final (Final)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Railway inyecta la variable de entorno 'PORT'. 
# Nuestro Program.cs se configurará para escuchar en esa variable.
ENTRYPOINT ["dotnet", "AppN8N.dll"]
