﻿using System.Text;

namespace EventILWeaver.Console.ListExistingAutoGeneratedEvents
{
    public class ListExistingAutoGeneratedEventsHandler: HandlerBase
    {
        private readonly string _ilWeavedAutoGeneratedEventAttributeName;

        public ListExistingAutoGeneratedEventsHandler(string ilWeavedAutoGeneratedEventAttributeName)
        {
            _ilWeavedAutoGeneratedEventAttributeName = ilWeavedAutoGeneratedEventAttributeName;
        }


        public int Run(ListExistingAutoGeneratedEventsOptions options)
        {
            var existingTypesWithAutoGeneratedEvents = GetExistingTypesWithAutoGeneratedEvents(options.TargetDllPath, _ilWeavedAutoGeneratedEventAttributeName);

            var sb = new StringBuilder($"Existing events which have {_ilWeavedAutoGeneratedEventAttributeName} attribute:\r\n\r\n");
            foreach (var existingTypeWithAutoGeneratedEvents in existingTypesWithAutoGeneratedEvents)
            {
                sb.AppendLine(existingTypeWithAutoGeneratedEvents.Type.Name);
                foreach (var ev in existingTypeWithAutoGeneratedEvents.EventsWithAutoGeneratedAttribute)
                {
                    sb.AppendLine("\t" + ev.FullName);
                }
            }

            System.Console.WriteLine(sb.ToString());

            return 0;
        }
    }
}
