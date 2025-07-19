using Curaitor.ProofOfConcept;
using LangChain.Databases;
using LangChain.Databases.Sqlite;
using LangChain.DocumentLoaders;
using LangChain.Extensions;
using LangChain.Providers;
using LangChain.Providers.Ollama;

// Please note, the code herein is a mess, it's insecure, it's almost certainly buggy.
// THIS IS NOT GOOD CODE
// It's just a PoC.

OllamaProvider provider = new();
OllamaChatModel llm = new(provider, "llama3.1:8b");
OllamaEmbeddingModel embeddingModel = new(provider, "nomic-embed-text:latest");
using SqLiteVectorDatabase vectorDatabase = new(dataSource: "vectors.db");
await vectorDatabase.AddDocumentsFromAsync<SpotifyTracksLoader>(embeddingModel, 768, DataSource.FromBytes([]), "tracks");

// This would be a vector db of all of the songs known about
IVectorCollection collection = await vectorDatabase.GetCollectionAsync("tracks");
const string question = "Make a playlist of ten metal songs with emo vibes";
IReadOnlyCollection<Document> similarDocuments = await collection.GetSimilarDocuments(embeddingModel, question, 100);

string prompt = $"""
                 Use the following pieces of context to answer the question at the end.
                  If the answer is not in context then just say that you don't know, don't try to make up an answer.
                  Keep the answer as short as possible.

                  {similarDocuments.AsString()}

                  Question: {question}
                  Helpful Answer:
                 """;

ChatResponse answer = await llm.GenerateAsync(prompt);

Console.WriteLine(answer);