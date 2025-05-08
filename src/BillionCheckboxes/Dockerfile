FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS publish

# Copy files
WORKDIR /src
COPY . .
ARG PROJECT_FILE="BillionCheckboxes.csproj"
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish $PROJECT_FILE -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final stage/image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS final

USER $APP_UID
WORKDIR /app
EXPOSE 8080

WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BillionCheckboxes.dll"]
