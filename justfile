default:
    @just --list

# start dev environment (frontend http://localhost:9876, backend http://localhost:9877/swagger)
up:
    docker compose -f docker-compose.dev.yml up -d --build

down:
    docker compose -f docker-compose.dev.yml down

# rebuild backend after C# changes
rebuild:
    docker compose -f docker-compose.dev.yml up -d --build server

logs service="server":
    docker compose -f docker-compose.dev.yml logs -f {{service}}

test *args:
    dotnet test Lingarr.slnx {{args}}

lint:
    docker exec Lingarr.Client npm run lint

format:
    docker exec Lingarr.Client npm run format
