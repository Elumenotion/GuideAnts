docker build -t dotnet-8.0-python-3.11 -f dockerfile .

docker run -d -i --name dotnet-8.0-python-3.11-container dotnet-8.0-python-3.11
