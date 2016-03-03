﻿using EventStore.ClientAPI;

namespace ServiceStack.EventStore.Repository
{
    using Types;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Idempotency;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;
    using Exceptions;
    using Text;

    public class EventStore : IEventStore
    {
        private const string EventClrTypeHeader = "EventClrTypeName";
        private const string AggregateClrTypeHeader = "AggregateClrTypeName";
        private const int WritePageSize = 500;
        private const int ReadPageSize = 500;

        private readonly Func<Type, Guid, string> aggregateIdToStreamName;
        private readonly IEventStoreConnection connection;

        public EventStore(IEventStoreConnection connection)
        {
            this.connection = connection;
        }

        public async void Publish(Event @event)
        {
            var streamName = @event.StreamName;

            var headers = new Dictionary<string, object>
                {
                    {EventClrTypeHeader, @event.GetType().Name}
                };

            await connection.AppendToStreamAsync(streamName, ExpectedVersion.Any, ToEventData(@event, headers));
        }

        public async void Publish(IAggregate aggregate)
        {
            var headers = new Dictionary<string, object>
                {
                    {AggregateClrTypeHeader, aggregate.GetType().Name}
                };

            var streamName = aggregateIdToStreamName(aggregate.GetType(), aggregate.Id);

            var newEvents = aggregate.GetUncommittedEvents().Cast<Event>().ToList();
            var originalVersion = aggregate.Version - newEvents.Count;
            var expectedVersion = originalVersion == 0 
                                    ? ExpectedVersion.NoStream 
                                    : originalVersion - 1;
            var eventsToSave = newEvents.Select(@event => ToEventData(@event, headers)).ToList();

            if (eventsToSave.Count < WritePageSize)
            {
                await connection.AppendToStreamAsync(streamName, expectedVersion, eventsToSave);
            }
            else
            {
                var transaction = await connection.StartTransactionAsync(streamName, expectedVersion);
                var position = 0;

                while (position < eventsToSave.Count)
                {
                    var pageEvents = eventsToSave.Skip(position).Take(WritePageSize);
                    await transaction.WriteAsync(pageEvents);
                    position += WritePageSize;
                }

                await transaction.CommitAsync();
            }
        }

        public async Task<TAggregate> GetById<TAggregate>(Guid id, int version) where TAggregate : class, IAggregate
        {
            if (version <= 0)
                throw new InvalidOperationException("Cannot get version <= 0");

            var streamName = aggregateIdToStreamName(typeof(TAggregate), id);
            var aggregate = ConstructAggregate<TAggregate>();

            var sliceStart = 1; //Ignores $StreamCreated
            StreamEventsSlice currentSlice;

            do
            {
                var sliceCount = sliceStart + ReadPageSize <= version
                                    ? ReadPageSize
                                    : version - sliceStart + 1;

                currentSlice = await connection.ReadStreamEventsForwardAsync(streamName, sliceStart, sliceCount, false);

                if (currentSlice.Status == SliceReadStatus.StreamNotFound)
                    throw new AggregateNotFoundException(id, typeof(TAggregate));

                if (currentSlice.Status == SliceReadStatus.StreamDeleted)
                    throw new AggregateDeletedException(id, typeof(TAggregate));

                sliceStart = currentSlice.NextEventNumber;

                foreach (var evnt in currentSlice.Events)
                    aggregate.ApplyEvent(DeserializeEvent(evnt.OriginalEvent.Metadata, evnt.OriginalEvent.Data));

            } while (version >= currentSlice.NextEventNumber && !currentSlice.IsEndOfStream);

            if (aggregate.Version != version && version < int.MaxValue)
                throw new AggregateVersionException(id, typeof(TAggregate), aggregate.Version, version);

            return aggregate;
        }

        private static object DeserializeEvent(byte[] metadata, byte[] data)
        {
            var eventClrTypeName = JsonObject.Parse(metadata.FromAsciiBytes()).GetUnescaped(EventClrTypeHeader);
            var serializer = new JsonStringSerializer();

            return serializer.DeserializeFromString(data.FromAsciiBytes(), Type.GetType(eventClrTypeName));
        }

        private static TAggregate ConstructAggregate<TAggregate>()
        {
            return (TAggregate)Activator.CreateInstance(typeof(TAggregate), true);
        }

        private EventData ToEventData(object @event, IDictionary<string, object> headers)
        {
            var json = @event.ToJson();
            var data = json.ToAsciiBytes();
            var deterministicEventId = GetDeterministicEventId(json);
            var typeName = @event.GetType().Name;

            var eventHeaders = new Dictionary<string, object>(headers)
            {
                {
                    EventClrTypeHeader, @event.GetType().AssemblyQualifiedName
                }
            };

            var metadata = eventHeaders.ToJson().ToAsciiBytes();

            return new EventData(deterministicEventId, typeName, true, data, metadata);
        }

        private Guid GetExecutingAssemblyGuid()
        {
            var assembly = GetType().Assembly;
            var attribute = (GuidAttribute)assembly.GetCustomAttributes(typeof(GuidAttribute), true)[0];
            var assemblyGuid = new Guid(attribute.Value);
            return assemblyGuid;
        }

        private Guid GetDeterministicEventId(string json)
        {
            var assemblyGuid = GetExecutingAssemblyGuid();
            var deterministicEventId = GuidUtility.Create(assemblyGuid, json);
            return deterministicEventId;
        }
    }
}
