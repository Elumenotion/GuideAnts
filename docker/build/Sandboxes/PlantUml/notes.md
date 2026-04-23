echo "@startuml\nAlice -> Bob: Hello\nBob --> Alice: Hi\n@enduml" > diagram.txt && java -jar plantuml.jar diagram.txt

printf "@startuml\nAlice -> Bob: Hello\nBob --> Alice: Hi\n@enduml\n" > diagram2.txt

docker build -t plantuml-1.2025.2 -f dockerfile .